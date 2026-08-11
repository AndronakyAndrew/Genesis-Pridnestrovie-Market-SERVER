using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Раскрытие контактов продавца: единственный эндпоинт, отдающий телефон;
/// анти-скрейпинг (rate-limit, журнал) и невидимость телефона в остальном API.
/// </summary>
public class ContactRevealTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";
    private const string SellerPhone = "+37312345678";
    private const string SellerDigits = "37312345678";

    [Fact]
    public async Task Phone_absent_from_catalog_and_detail()
    {
        var ownerId = await factory.SeedUserAsync(Unique("owner"), Password);
        var listingId = await factory.SeedListingAsync(ownerId);

        var client = factory.CreateClient();

        // Каталог целиком.
        var catalog = await client.GetStringAsync("/api/listings");
        Assert.DoesNotContain(SellerDigits, catalog);

        // Карточка объявления.
        var detailRaw = await client.GetStringAsync($"/api/listings/{listingId}");
        Assert.DoesNotContain(SellerDigits, detailRaw);

        // Но агрегат раскрытий в карточке присутствует.
        using var doc = JsonDocument.Parse(detailRaw);
        Assert.True(doc.RootElement.TryGetProperty("contactRevealCount", out var count));
        Assert.Equal(0, count.GetInt32());
    }

    [Fact]
    public async Task Contact_returns_phone_and_server_built_deeplinks()
    {
        var ownerId = await factory.SeedUserAsync(Unique("seller"), Password);
        await factory.ConfigureContactAsync(ownerId, showPhone: true,
            telegram: "seller_ivan", viber: true, whatsapp: true);
        var listingId = await factory.SeedListingAsync(ownerId);

        var viewer = Unique("viewer");
        await factory.SeedUserAsync(viewer, Password);
        var client = await AuthedClient(viewer);

        var resp = await client.GetAsync($"/api/listings/{listingId}/contact");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(SellerPhone, body.GetProperty("phone").GetString());
        Assert.Equal("https://t.me/seller_ivan", body.GetProperty("telegramUrl").GetString());
        Assert.Equal($"viber://chat?number=%2B{SellerDigits}", body.GetProperty("viberUrl").GetString());
        Assert.Equal($"https://wa.me/{SellerDigits}", body.GetProperty("whatsappUrl").GetString());

        // Раскрытие записано в журнал.
        Assert.Equal(1, await factory.ContactRevealCountAsync(listingId));
    }

    [Fact]
    public async Task Contact_omits_disabled_channels()
    {
        var ownerId = await factory.SeedUserAsync(Unique("seller-min"), Password);
        // showPhone=true, но мессенджеры выключены и telegram не задан.
        await factory.ConfigureContactAsync(ownerId, showPhone: true);
        var listingId = await factory.SeedListingAsync(ownerId);

        var viewer = Unique("viewer-min");
        await factory.SeedUserAsync(viewer, Password);
        var client = await AuthedClient(viewer);

        var body = await (await client.GetAsync($"/api/listings/{listingId}/contact"))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(SellerPhone, body.GetProperty("phone").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("telegramUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("viberUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("whatsappUrl").ValueKind);
    }

    [Fact]
    public async Task Contact_returns_404_when_show_phone_disabled()
    {
        var ownerId = await factory.SeedUserAsync(Unique("hidden"), Password);
        await factory.ConfigureContactAsync(ownerId, showPhone: false);
        var listingId = await factory.SeedListingAsync(ownerId);

        var viewer = Unique("viewer-hidden");
        await factory.SeedUserAsync(viewer, Password);
        var client = await AuthedClient(viewer);

        var resp = await client.GetAsync($"/api/listings/{listingId}/contact");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        // Скрытое раскрытие не журналируется.
        Assert.Equal(0, await factory.ContactRevealCountAsync(listingId));
    }

    [Fact]
    public async Task Anonymous_rate_limit_exceeded_returns_429()
    {
        var ownerId = await factory.SeedUserAsync(Unique("rl-owner"), Password);
        var listingId = await factory.SeedListingAsync(ownerId);

        var client = factory.CreateClient(); // аноним: лимит 10/час на IpHash

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
            statuses.Add((await client.GetAsync($"/api/listings/{listingId}/contact")).StatusCode);

        // Первые запросы проходят, лимит срабатывает на 11-м.
        Assert.Equal(HttpStatusCode.OK, statuses[0]);
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[10]);

        // 429 несёт Retry-After.
        var limited = await client.GetAsync($"/api/listings/{listingId}/contact");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
    }

    // ---- helpers ----

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";

    private async Task<HttpClient> AuthedClient(string email)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }
}
