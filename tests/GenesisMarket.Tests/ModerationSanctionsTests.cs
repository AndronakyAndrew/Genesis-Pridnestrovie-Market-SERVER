using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Domain.Enums;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Санкции модератора: предупреждение (без бана) и чёрный список карт. Номер карты
/// принимается один раз и нигде не хранится: в БД, ответах и журнале — только
/// HMAC и последние 4 цифры. Карта из списка в тексте объявления уводит его на
/// премодерацию даже у доверенного автора.
/// </summary>
public class ModerationSanctionsTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    // ---- предупреждение ----

    [Fact]
    public async Task Warn_counts_logs_and_shows_in_dossier_without_ban()
    {
        var (mod, _) = await Moderator();
        var userId = await factory.SeedUserAsync(Unique("warned"), Password);

        for (var i = 0; i < 2; i++)
        {
            var resp = await mod.PostAsJsonAsync($"/api/moderation/users/{userId}/warn",
                new { reason = $"Спам в описаниях #{i}" });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        Assert.Equal(2, await factory.ModerationLogCountAsync(userId, "user.warn"));
        Assert.False((await factory.UserBanStateAsync(userId)).IsBanned);

        using var doc = await GetJson(mod, $"/api/moderation/users/{userId}/dossier");
        Assert.Equal(2, doc.RootElement.GetProperty("user").GetProperty("warningsCount").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("sanctions").EnumerateArray()
            .Count(s => s.GetProperty("action").GetString() == "user.warn"));

        var code = doc.RootElement.GetProperty("user").GetProperty("publicCode").GetString();
        using var warned = await GetJson(mod, $"/api/moderation/users?tab=Warned&q={code}");
        Assert.Single(warned.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Warn_rejects_self_admin_and_empty_reason()
    {
        var (mod, modId) = await Moderator();
        var adminId = await factory.SeedUserAsync(Unique("admin"), Password, role: UserRole.Admin);
        var userId = await factory.SeedUserAsync(Unique("user"), Password);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await mod.PostAsJsonAsync($"/api/moderation/users/{modId}/warn", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await mod.PostAsJsonAsync($"/api/moderation/users/{adminId}/warn", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await mod.PostAsJsonAsync($"/api/moderation/users/{userId}/warn", new { reason = "" })).StatusCode);
    }

    // ---- чёрный список карт ----

    [Fact]
    public async Task Block_card_stores_only_last4_and_logs_without_number()
    {
        var (mod, _) = await Moderator();
        var number = RandomCard("9005");

        var resp = await mod.PostAsJsonAsync("/api/moderation/blocked-cards",
            new { cardNumber = Spaced(number), reason = "Требовал предоплату на карту" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain(number, body);
        using var created = JsonDocument.Parse(body);
        var id = created.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(number[^4..], created.RootElement.GetProperty("last4").GetString());

        // Журнал: запись есть, номера в ней нет, подпись — последние цифры.
        using var log = await GetJson(mod, $"/api/moderation/logs?q={id}");
        var entry = Assert.Single(log.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("card.block", entry.GetProperty("action").GetString());
        Assert.Equal("…" + number[^4..], entry.GetProperty("target").GetProperty("label").GetString());
        Assert.DoesNotContain(number, entry.GetRawText());

        // Тот же номер в другом написании — дубликат.
        var dup = await mod.PostAsJsonAsync("/api/moderation/blocked-cards",
            new { cardNumber = string.Join("-", Chunks(number)), reason = "Повтор" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        using var list = await GetJson(mod, $"/api/moderation/blocked-cards?last4={number[^4..]}");
        Assert.Contains(list.RootElement.GetProperty("items").EnumerateArray(), c => c.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task Block_card_rejects_too_short_number()
    {
        var (mod, _) = await Moderator();
        var resp = await mod.PostAsJsonAsync("/api/moderation/blocked-cards",
            new { cardNumber = "1234 5678 90", reason = "Мало цифр" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Listing_with_blocked_card_goes_to_premoderation_even_for_trusted_author()
    {
        var (mod, _) = await Moderator();
        var number = RandomCard("4276");
        var block = await mod.PostAsJsonAsync("/api/moderation/blocked-cards",
            new { cardNumber = number, reason = "Мошенничество" });
        Assert.Equal(HttpStatusCode.Created, block.StatusCode);
        using var blocked = JsonDocument.Parse(await block.Content.ReadAsStringAsync());
        var blockedId = blocked.RootElement.GetProperty("id").GetGuid();

        var email = Unique("trusted");
        var authorId = await factory.SeedUserAsync(email, Password);
        await factory.SetAuthorTrustAsync(authorId, approvedListings: 5, createdAt: DateTimeOffset.UtcNow.AddDays(-60));
        var author = await AuthedClient(email);

        // Доверенный автор без карты в тексте публикуется сразу (контроль).
        var clean = await Publish(author, "Велосипед горный взрослый", "Хорошее состояние, самовывоз из центра города");
        Assert.Equal("Active", clean.GetProperty("status").GetString());

        // Та же публикация с картой из списка — премодерация и приоритет автофлага.
        var withCard = await Publish(author, "Велосипед горный детский",
            $"Предоплата на карту {Spaced(number)}, отправлю такси");
        Assert.Equal("PendingReview", withCard.GetProperty("status").GetString());
        var listingId = withCard.GetProperty("id").GetGuid();
        Assert.Equal(100, (await factory.ListingModerationAsync(listingId)).ModerationPriority);

        // Карту убрали — правило больше не действует.
        Assert.Equal(HttpStatusCode.NoContent,
            (await mod.DeleteAsync($"/api/moderation/blocked-cards/{blockedId}")).StatusCode);
        var after = await Publish(author, "Велосипед горный подростковый",
            $"Предоплата на карту {Spaced(number)}, отправлю такси");
        Assert.NotEqual(100, (await factory.ListingModerationAsync(after.GetProperty("id").GetGuid())).ModerationPriority);
    }

    // ---- helpers ----

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";

    /// <summary>Случайный 16-значный номер с заданным префиксом — чтобы тесты не делили записи списка.</summary>
    private static string RandomCard(string prefix) =>
        prefix + string.Concat(Enumerable.Range(0, 12).Select(_ => Random.Shared.Next(10)));

    private static IEnumerable<string> Chunks(string number) => number.Chunk(4).Select(c => new string(c));

    private static string Spaced(string number) => string.Join(" ", Chunks(number));

    private async Task<(HttpClient Client, Guid Id)> Moderator()
    {
        var email = Unique("mod");
        var id = await factory.SeedUserAsync(email, Password, role: UserRole.Moderator);
        return (await AuthedClient(email), id);
    }

    private async Task<HttpClient> AuthedClient(string email)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new("Bearer", doc.RootElement.GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task<JsonElement> Publish(HttpClient client, string title, string description)
    {
        var resp = await client.PostAsJsonAsync("/api/listings", new
        {
            title,
            description,
            price = 1500,
            priceType = "fixed",
            category = "home",
            subcategoryId = 18,
            city = "tiraspol",
            district = (string?)null,
            condition = "used",
            publish = true
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static async Task<JsonDocument> GetJson(HttpClient client, string url)
    {
        var resp = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    }
}
