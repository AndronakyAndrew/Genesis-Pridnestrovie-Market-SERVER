using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Список активных сессий и отзыв отдельной сессии (вкладка «Безопасность»).
/// Это поверхность, через которую утекают данные о перемещениях владельца,
/// поэтому половина проверок — про то, чего в ответе быть НЕ должно.
/// </summary>
public class SessionsTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    private const string ChromeWindows =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    private const string SafariIphone =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.2 Mobile/15E148 Safari/604.1";

    /// <summary>
    /// Два входа с разных устройств — две сессии. Отзыв одной не трогает вторую:
    /// вторая продолжает работать и остаётся в списке.
    /// </summary>
    [Fact]
    public async Task Revoking_one_session_leaves_the_other_working()
    {
        var email = Unique("sessions-two");
        await factory.SeedUserAsync(email, Password);

        var desktop = await SignInAsync(email, ChromeWindows);
        var phone = await SignInAsync(email, SafariIphone);

        // У телефона в списке — обе сессии, своя помечена текущей.
        var sessions = await phone.Client.GetFromJsonAsync<JsonElement>("/api/me/sessions");
        Assert.Equal(2, sessions.GetArrayLength());

        var mine = Single(sessions, s => s.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(phone.SessionId, mine.GetProperty("id").GetGuid());
        Assert.Equal("Телефон", mine.GetProperty("deviceFamily").GetString());
        Assert.Equal("Safari", mine.GetProperty("browserFamily").GetString());
        Assert.Equal("iOS", mine.GetProperty("osFamily").GetString());

        var other = Single(sessions, s => !s.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(desktop.SessionId, other.GetProperty("id").GetGuid());
        Assert.Equal("Компьютер", other.GetProperty("deviceFamily").GetString());
        Assert.Equal("Chrome", other.GetProperty("browserFamily").GetString());
        Assert.Equal("Windows", other.GetProperty("osFamily").GetString());

        // Телефон отзывает сессию компьютера.
        var revoke = await phone.Client.DeleteAsync($"/api/me/sessions/{desktop.SessionId}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        // Своя сессия жива: и запрос проходит, и в списке осталась одна запись.
        var after = await phone.Client.GetFromJsonAsync<JsonElement>("/api/me/sessions");
        Assert.Equal(1, after.GetArrayLength());
        Assert.Equal(phone.SessionId, after[0].GetProperty("id").GetGuid());
        Assert.True(after[0].GetProperty("isCurrent").GetBoolean());

        // А refresh-токен отозванной сессии больше не обменивается.
        var refreshDead = await factory.CreateClient().PostAsJsonAsync(
            "/api/auth/refresh", new { refreshToken = desktop.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refreshDead.StatusCode);

        // Токен живой сессии — обменивается.
        var refreshAlive = await factory.CreateClient().PostAsJsonAsync(
            "/api/auth/refresh", new { refreshToken = phone.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshAlive.StatusCode);
    }

    /// <summary>
    /// Чужую сессию отозвать нельзя, и ответ — 404, а не 403: 403 подтверждал бы,
    /// что такой идентификатор существует.
    /// </summary>
    [Fact]
    public async Task Revoking_a_foreign_session_returns_404_and_does_not_touch_it()
    {
        var victimEmail = Unique("sessions-victim");
        await factory.SeedUserAsync(victimEmail, Password);
        var victim = await SignInAsync(victimEmail, ChromeWindows);

        var attackerEmail = Unique("sessions-attacker");
        await factory.SeedUserAsync(attackerEmail, Password);
        var attacker = await SignInAsync(attackerEmail, ChromeWindows);

        var resp = await attacker.Client.DeleteAsync($"/api/me/sessions/{victim.SessionId}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        // Сессия жертвы цела.
        var victimSessions = await victim.Client.GetFromJsonAsync<JsonElement>("/api/me/sessions");
        Assert.Equal(1, victimSessions.GetArrayLength());
        Assert.Equal(victim.SessionId, victimSessions[0].GetProperty("id").GetGuid());

        // Несуществующая сессия — тот же ответ, без различий.
        var missing = await attacker.Client.DeleteAsync($"/api/me/sessions/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    /// <summary>
    /// В ответе нет ни полного IP, ни сырого User-Agent — ни в одном поле.
    /// Проверяем и выдачу, и саму БД: сырой User-Agent не должен существовать
    /// даже в хранилище, а полный адрес лежит только как HMAC.
    /// </summary>
    [Fact]
    public async Task Sessions_expose_neither_full_ip_nor_raw_user_agent()
    {
        var email = Unique("sessions-leak");
        var userId = await factory.SeedUserAsync(email, Password);
        var session = await SignInAsync(email, ChromeWindows);

        var raw = await session.Client.GetStringAsync("/api/me/sessions");

        // Сырой User-Agent целиком и его характерные куски.
        Assert.DoesNotContain(ChromeWindows, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Mozilla", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AppleWebKit", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("141.0.0.0", raw, StringComparison.Ordinal); // версия сборки

        // Хеш IP наружу не выходит ни под каким именем.
        Assert.DoesNotContain("ipHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("createdByIpHash", raw, StringComparison.OrdinalIgnoreCase);

        // Геолокацию не реализуем — город всегда null (Pass 2).
        var sessions = await session.Client.GetFromJsonAsync<JsonElement>("/api/me/sessions");
        Assert.Equal(JsonValueKind.Null, sessions[0].GetProperty("city").ValueKind);

        // ipPrefix — не полный адрес: для IPv4 ровно два октета, не четыре.
        var prefix = sessions[0].GetProperty("ipPrefix").GetString();
        if (prefix is not null && prefix.Contains('.'))
            Assert.Equal(2, prefix.Split('.').Length);

        // В БД сырого User-Agent нет: такой колонки не существует, а разобранные
        // семейства — короткие значения без версий.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .Select(t => new { t.DeviceFamily, t.BrowserFamily, t.OsFamily, t.IpPrefix, t.CreatedByIpHash })
            .FirstAsync();

        Assert.Equal("Компьютер", stored.DeviceFamily);
        Assert.Equal("Chrome", stored.BrowserFamily);
        Assert.Equal("Windows", stored.OsFamily);
        // Полный адрес есть только в виде HMAC (hex), если ключ задан.
        Assert.True(stored.CreatedByIpHash is null || stored.CreatedByIpHash.Length == 64);
    }

    /// <summary>
    /// Сессия переживает ротацию refresh-токена: идентификатор и время начала
    /// не меняются, хотя строка токена в БД уже другая.
    /// </summary>
    [Fact]
    public async Task Session_survives_refresh_token_rotation()
    {
        var email = Unique("sessions-rotate");
        await factory.SeedUserAsync(email, Password);
        var session = await SignInAsync(email, ChromeWindows);

        var before = await session.Client.GetFromJsonAsync<JsonElement>("/api/me/sessions");
        var startedAt = before[0].GetProperty("createdAt").GetDateTimeOffset();

        // Ротация.
        var refreshed = await factory.CreateClient().PostAsJsonAsync(
            "/api/auth/refresh", new { refreshToken = session.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var body = await refreshed.Content.ReadFromJsonAsync<JsonElement>();

        var rotated = factory.CreateClient();
        rotated.DefaultRequestHeaders.Authorization =
            new("Bearer", body.GetProperty("accessToken").GetString());

        var after = await rotated.GetFromJsonAsync<JsonElement>("/api/me/sessions");
        Assert.Equal(1, after.GetArrayLength());                       // не две «сессии»
        Assert.Equal(session.SessionId, after[0].GetProperty("id").GetGuid());
        Assert.Equal(startedAt, after[0].GetProperty("createdAt").GetDateTimeOffset());
        Assert.True(after[0].GetProperty("isCurrent").GetBoolean());
    }

    /// <summary>Отзыв своей текущей сессии разрешён и равносилен выходу.</summary>
    [Fact]
    public async Task Revoking_the_current_session_kills_its_refresh_token()
    {
        var email = Unique("sessions-self");
        await factory.SeedUserAsync(email, Password);
        var session = await SignInAsync(email, ChromeWindows);

        var resp = await session.Client.DeleteAsync($"/api/me/sessions/{session.SessionId}");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        var refresh = await factory.CreateClient().PostAsJsonAsync(
            "/api/auth/refresh", new { refreshToken = session.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        // Повторный отзыв той же сессии — 404: активной строки уже нет.
        var again = await session.Client.DeleteAsync($"/api/me/sessions/{session.SessionId}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    /// <summary>
    /// Регрессия. Отозванное устройство само пойдёт обновлять токен — оно ещё не
    /// знает, что его выключили. Этот обмен обязан просто провалиться (401) и НЕ
    /// трактоваться как кража: иначе «отзыв цепочки» разлогинивал бы владельца со
    /// всех устройств через несколько минут после отзыва одного, и кнопка «выйти
    /// на этом устройстве» работала бы как «выйти везде».
    /// </summary>
    [Fact]
    public async Task Revoked_device_retrying_its_token_does_not_kill_the_other_sessions()
    {
        var email = Unique("sessions-retry");
        await factory.SeedUserAsync(email, Password);

        var desktop = await SignInAsync(email, ChromeWindows);
        var phone = await SignInAsync(email, SafariIphone);

        Assert.Equal(HttpStatusCode.NoContent,
            (await phone.Client.DeleteAsync($"/api/me/sessions/{desktop.SessionId}")).StatusCode);

        // Отключённый компьютер повторяет попытку — и не один раз.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var retry = await factory.CreateClient().PostAsJsonAsync(
                "/api/auth/refresh", new { refreshToken = desktop.RefreshToken });
            Assert.Equal(HttpStatusCode.Unauthorized, retry.StatusCode);
        }

        // Сессия телефона не пострадала: и список читается, и токен обменивается.
        var sessions = await phone.Client.GetFromJsonAsync<JsonElement>("/api/me/sessions");
        Assert.Equal(1, sessions.GetArrayLength());

        var refresh = await factory.CreateClient().PostAsJsonAsync(
            "/api/auth/refresh", new { refreshToken = phone.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
    }

    /// <summary>
    /// Защита от кражи на месте: предъявление СТАРОГО токена из середины цепочки
    /// (такой отозван ротацией, а не владельцем) по-прежнему отзывает всё.
    /// Окно гонки вкладок этого не отменяет — за его пределами реакция прежняя.
    /// </summary>
    [Fact]
    public async Task Replaying_a_rotated_away_token_still_revokes_every_session()
    {
        var email = Unique("sessions-theft");
        var userId = await factory.SeedUserAsync(email, Password);

        var stolen = await SignInAsync(email, ChromeWindows);
        var other = await SignInAsync(email, SafariIphone);

        // Законная ротация: прежний токен становится «замещённым».
        var rotated = await factory.CreateClient().PostAsJsonAsync(
            "/api/auth/refresh", new { refreshToken = stolen.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);

        // Сдвигаем отзыв в прошлое: повтор идёт ЗА окном гонки вкладок.
        await factory.ExecuteSqlAsync(
            "UPDATE refresh_tokens SET \"RevokedAt\" = \"RevokedAt\" - interval '1 hour' " +
            $"WHERE \"RevokedAt\" IS NOT NULL AND \"UserId\" = '{userId}'");

        // Вор предъявляет украденный старый токен.
        var replay = await factory.CreateClient().PostAsJsonAsync(
            "/api/auth/refresh", new { refreshToken = stolen.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // Реакция — отзыв ВСЕХ сессий пользователя, включая непричастную.
        var afterTheft = await factory.CreateClient().PostAsJsonAsync(
            "/api/auth/refresh", new { refreshToken = other.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterTheft.StatusCode);
    }

    /// <summary>Список сессий закрыт авторизацией.</summary>
    [Fact]
    public async Task Sessions_require_authentication()
    {
        var resp = await factory.CreateClient().GetAsync("/api/me/sessions");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ---- helpers ----

    private sealed record SignedIn(HttpClient Client, Guid SessionId, string RefreshToken);

    /// <summary>Вход с конкретным User-Agent — так в тесте появляются разные устройства.</summary>
    private async Task<SignedIn> SignInAsync(string email, string userAgent)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        var access = body.GetProperty("accessToken").GetString()!;
        var refresh = body.GetProperty("refreshToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", access);

        return new SignedIn(client, SessionIdOf(access), refresh);
    }

    /// <summary>Достаёт claim sid из access-токена (payload — вторая часть JWT).</summary>
    private static Guid SessionIdOf(string accessToken)
    {
        var payload = accessToken.Split('.')[1];
        var padded = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')
            .Replace('-', '+').Replace('_', '/');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(padded));
        return doc.RootElement.GetProperty("sid").GetGuid();
    }

    private static JsonElement Single(JsonElement array, Func<JsonElement, bool> predicate) =>
        array.EnumerateArray().Single(predicate);

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";
}
