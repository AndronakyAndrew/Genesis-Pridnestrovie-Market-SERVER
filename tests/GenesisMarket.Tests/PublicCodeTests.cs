using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Публичный номер аккаунта («ID профиля», он же GEN-82914 на фронте):
/// выдача при регистрации, поведение при коллизии, видимость в API.
/// </summary>
public class PublicCodeTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    /// <summary>
    /// Коллизия: источник дважды подсовывает уже занятый код. Регистрация обязана
    /// пройти — генератор просто берёт следующий. Без этого теста поломка
    /// проявилась бы примерно раз на 90 000 регистраций и выглядела бы как
    /// «иногда не регистрируется».
    /// </summary>
    [Fact]
    public async Task Registration_succeeds_when_generator_hits_an_occupied_code_twice()
    {
        // Занятый код: берём у существующего пользователя.
        var occupiedEmail = Unique("code-taken");
        var occupiedId = await factory.SeedUserAsync(occupiedEmail, Password);
        var occupiedCode = await CodeOfAsync(occupiedId);

        // Свободный код подбираем так, чтобы он гарантированно не был занят.
        var freeCode = await FreeCodeAsync();

        // Две первые попытки — в занятый код, третья — в свободный.
        factory.PublicCodes.Script(occupiedCode, occupiedCode, freeCode);

        var email = Unique("code-collision");
        var register = await factory.CreateClient().PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = Password,
            displayName = "После коллизии",
            city = nameof(City.Tiraspol),
            phone = "+37377112233"
        });

        Assert.Equal(HttpStatusCode.OK, register.StatusCode);

        // Аккаунт создан и получил именно третий, свободный код.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var created = await db.Users.AsNoTracking()
            .FirstAsync(u => u.Email == email.ToLowerInvariant());
        Assert.Equal(freeCode, created.PublicCode);

        // И источник действительно дёрнули трижды — коллизия была настоящей,
        // а не «тест прошёл, потому что скрипт никто не читал».
        Assert.Equal(
            [occupiedCode, occupiedCode, freeCode],
            factory.PublicCodes.Issued.TakeLast(3));

        // Занятый код остался у прежнего владельца.
        Assert.Equal(occupiedCode, await CodeOfAsync(occupiedId));
    }

    /// <summary>
    /// Исчерпание попыток: все 10 подряд — в занятый код. Регистрация падает,
    /// а не выдаёт дубликат. 500 здесь правильный ответ: это отказ сервера
    /// подобрать код, а не ошибка в запросе клиента.
    /// </summary>
    [Fact]
    public async Task Registration_fails_when_every_attempt_hits_an_occupied_code()
    {
        var occupiedId = await factory.SeedUserAsync(Unique("code-exhaust-seed"), Password);
        var occupiedCode = await CodeOfAsync(occupiedId);

        factory.PublicCodes.Script(Enumerable.Repeat(occupiedCode, 10).ToArray());

        var email = Unique("code-exhaust");
        var register = await factory.CreateClient().PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = Password,
            displayName = "Некуда деться",
            city = nameof(City.Tiraspol),
            phone = "+37377112244"
        });

        Assert.Equal(HttpStatusCode.InternalServerError, register.StatusCode);

        // Пользователь не создан — половинчатой записи без кода не осталось.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Users.AnyAsync(u => u.Email == email.ToLowerInvariant()));
    }

    /// <summary>Код — ровно 5 цифр из диапазона 10000–99999, без ведущего нуля.</summary>
    [Fact]
    public async Task Issued_code_is_five_digits_in_range()
    {
        var userId = await factory.SeedUserAsync(Unique("code-shape"), Password);
        var code = await CodeOfAsync(userId);

        Assert.Equal(5, code.Length);
        Assert.All(code, c => Assert.InRange(c, '0', '9'));
        Assert.InRange(int.Parse(code), 10_000, 99_999);
    }

    /// <summary>
    /// Код — публичное имя аккаунта: владелец видит его в /api/me, любой
    /// посетитель — в публичном профиле продавца. Один и тот же код в обоих
    /// ответах: именно им покупатель называет продавца в поддержке.
    /// </summary>
    [Fact]
    public async Task Code_is_the_same_for_owner_and_for_anonymous_visitor()
    {
        var email = Unique("code-visibility");
        var userId = await factory.SeedUserAsync(email, Password);
        var expected = await CodeOfAsync(userId);

        var client = await AuthedClient(email);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal(expected, me.GetProperty("publicCode").GetString());

        var publicProfile = await factory.CreateClient()
            .GetFromJsonAsync<JsonElement>($"/api/users/{userId}/public");
        Assert.Equal(expected, publicProfile.GetProperty("publicCode").GetString());

        // Публикация кода не тянет за собой остальное: профиль по-прежнему
        // без почты и телефона.
        var raw = await factory.CreateClient().GetStringAsync($"/api/users/{userId}/public");
        Assert.DoesNotContain(email, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phoneE164", raw, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Модератор находит аккаунт по коду — тот сценарий, ради которого код и
    /// заводился: в жалобе написано «GEN-82914», а не GUID.
    /// </summary>
    [Fact]
    public async Task Moderator_can_find_a_user_by_code()
    {
        var email = Unique("code-lookup");
        var userId = await factory.SeedUserAsync(email, Password);
        var code = await CodeOfAsync(userId);

        var moderator = Unique("code-moderator");
        await factory.SeedUserAsync(moderator, Password, role: UserRole.Moderator);
        var client = await AuthedClient(moderator);

        var card = await client.GetFromJsonAsync<JsonElement>($"/api/moderation/users/by-code/{code}");
        Assert.Equal(userId, card.GetProperty("id").GetGuid());
        Assert.Equal(code, card.GetProperty("publicCode").GetString());
        Assert.Equal(email.ToLowerInvariant(), card.GetProperty("email").GetString());

        // Несуществующий код — 404, а не пустая карточка.
        var missing = await client.GetAsync($"/api/moderation/users/by-code/{await FreeCodeAsync()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    /// <summary>
    /// Поиск по коду — только модератору. Иначе 90 000 значений превращаются
    /// в перечислимое пространство имён и обходом «10000…99999» выгружается
    /// список всех аккаунтов площадки.
    /// </summary>
    [Fact]
    public async Task Lookup_by_code_is_closed_to_regular_users_and_anonymous()
    {
        var email = Unique("code-lookup-denied");
        var userId = await factory.SeedUserAsync(email, Password);
        var code = await CodeOfAsync(userId);

        var anonymous = await factory.CreateClient().GetAsync($"/api/moderation/users/by-code/{code}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var client = await AuthedClient(email);
        var asUser = await client.GetAsync($"/api/moderation/users/by-code/{code}");
        Assert.Equal(HttpStatusCode.Forbidden, asUser.StatusCode);
    }

    /// <summary>Код выдаётся один раз: правка профиля его не трогает.</summary>
    [Fact]
    public async Task Code_does_not_change_when_profile_is_updated()
    {
        var email = Unique("code-stable");
        var userId = await factory.SeedUserAsync(email, Password);
        var before = await CodeOfAsync(userId);

        var client = await AuthedClient(email);
        var patch = await client.PatchAsJsonAsync("/api/me", new { displayName = "Новое имя" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        Assert.Equal(before, await CodeOfAsync(userId));
        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal(before, me.GetProperty("publicCode").GetString());
    }

    /// <summary>
    /// Регрессия на утечку даты регистрации: Guid пользователя — это UUID v7,
    /// первые 48 бит которого содержат unix-время создания в миллисекундах.
    /// Публичный профиль при этом намеренно округляет дату регистрации до месяца,
    /// так что Guid в публичном ответе обесценивал это округление. Ни в одном
    /// публичном ответе про продавца его быть не должно.
    /// </summary>
    [Fact]
    public async Task Public_responses_never_contain_the_owner_guid()
    {
        var email = Unique("code-no-guid");
        var userId = await factory.SeedUserAsync(email, Password);
        var code = await CodeOfAsync(userId);
        var listingId = await factory.SeedListingAsync(userId);
        var guid = userId.ToString();

        var anonymous = factory.CreateClient();

        var profile = await anonymous.GetStringAsync($"/api/users/by-code/{code}/public");
        Assert.DoesNotContain(guid, profile, StringComparison.OrdinalIgnoreCase);

        var listing = await anonymous.GetStringAsync($"/api/listings/{listingId}");
        Assert.DoesNotContain(guid, listing, StringComparison.OrdinalIgnoreCase);
        // Вместо Guid владельца — его «ID профиля».
        Assert.Equal(code, JsonDocument.Parse(listing).RootElement
            .GetProperty("ownerPublicCode").GetString());

        var reviews = await anonymous.GetStringAsync($"/api/users/by-code/{code}/reviews");
        Assert.DoesNotContain(guid, reviews, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Старые ссылки вида /user/{guid} уже расшарены — legacy-адрес обязан
    /// продолжать отвечать, иначе смена схемы ломает чужие закладки.
    /// </summary>
    [Fact]
    public async Task Legacy_guid_route_still_serves_the_public_profile()
    {
        var userId = await factory.SeedUserAsync(Unique("code-legacy"), Password);
        var code = await CodeOfAsync(userId);

        var legacy = await factory.CreateClient()
            .GetFromJsonAsync<JsonElement>($"/api/users/{userId}/public");

        Assert.Equal(code, legacy.GetProperty("publicCode").GetString());
        // Но и он отдаёт адрес аватара уже по коду — новых Guid наружу не появляется.
        Assert.DoesNotContain(userId.ToString(),
            legacy.GetProperty("avatarUrl").GetString() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Жалоба на продавца по «ID профиля»: Guid продавца клиенту больше неоткуда
    /// взять, и без этого пути кнопка «пожаловаться» на профиле перестала бы работать.
    /// </summary>
    [Fact]
    public async Task User_can_be_reported_by_public_code()
    {
        var targetId = await factory.SeedUserAsync(Unique("code-report-target"), Password);
        var code = await CodeOfAsync(targetId);

        var reporter = Unique("code-reporter");
        await factory.SeedUserAsync(reporter, Password);
        var client = await AuthedClient(reporter);

        var report = await client.PostAsJsonAsync("/api/reports", new
        {
            targetType = nameof(ReportTargetType.User),
            targetPublicCode = code,
            reason = nameof(ReportReason.Fraud),
            comment = "Просит предоплату и пропадает"
        });

        Assert.Equal(HttpStatusCode.Created, report.StatusCode);

        var body = await report.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("targetPublicCode").GetString());
        // Эхо не возвращает Guid цели — иначе мы бы отдали его тем же ответом.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("targetId").ValueKind);

        // Жалоба действительно записана на этого пользователя.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Reports.AnyAsync(
            r => r.TargetType == ReportTargetType.User && r.TargetId == targetId));
    }

    // ---- helpers ----

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";

    private async Task<string> CodeOfAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users.AsNoTracking()
            .Where(u => u.Id == userId).Select(u => u.PublicCode).FirstAsync();
    }

    /// <summary>Код, которого заведомо нет в базе.</summary>
    private async Task<string> FreeCodeAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var taken = await db.Users.AsNoTracking().Select(u => u.PublicCode).ToListAsync();

        for (var candidate = 10_000; candidate <= 99_999; candidate++)
        {
            var code = candidate.ToString();
            if (!taken.Contains(code))
                return code;
        }

        throw new InvalidOperationException("Свободных кодов не осталось — диапазон исчерпан.");
    }

    private async Task<HttpClient> AuthedClient(string email)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", doc.RootElement.GetProperty("accessToken").GetString());
        return client;
    }
}
