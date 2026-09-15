using System.Net;
using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Listings;
using GenesisMarket.Domain.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Короткая ссылка /r/l/{id}: запись перехода и 302 на карточку. Нужен Docker (PostgreSQL).
/// </summary>
public class LinkRedirectTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Utm = "?utm_source=telegram&utm_medium=channel&utm_campaign=listing";

    [Fact]
    public async Task Active_listing_records_click_and_redirects_to_card_with_utm()
    {
        var listingId = await SeedListingAsync("active");
        var slug = await factory.ListingSlugAsync(listingId);

        var resp = await Client().GetAsync($"/r/l/{listingId}?s=tg");

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal($"/listing/{slug}", resp.Headers.Location!.AbsolutePath);
        Assert.Equal(Utm, resp.Headers.Location.Query);
        Assert.Contains("no-store", resp.Headers.CacheControl?.ToString());

        var click = Assert.Single(await factory.LinkClicksAsync(listingId));
        Assert.Equal(LinkSources.Telegram, click.Source);
        // HMAC-SHA256 в hex (ключ задан в тестах), не сырой IP и не заглушка.
        Assert.Matches("^[0-9A-F]{64}$", click.IpHash);
    }

    [Fact]
    public async Task Head_redirects_to_card_without_recording_click()
    {
        var listingId = await SeedListingAsync("head");
        var slug = await factory.ListingSlugAsync(listingId);

        var resp = await Client().SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/r/l/{listingId}?s=tg"));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal($"/listing/{slug}", resp.Headers.Location!.AbsolutePath);
        Assert.Equal(Utm, resp.Headers.Location.Query);
        Assert.Empty(await factory.LinkClicksAsync(listingId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("?s=vk")]
    [InlineData("?s=%3Cscript%3E")]
    public async Task Source_outside_whitelist_is_stored_as_unknown(string query)
    {
        var listingId = await SeedListingAsync("source");

        var resp = await Client().GetAsync($"/r/l/{listingId}{query}");

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal(LinkSources.Unknown, Assert.Single(await factory.LinkClicksAsync(listingId)).Source);
    }

    [Fact]
    public async Task Repeated_click_within_window_is_recorded_once()
    {
        var listingId = await SeedListingAsync("repeat");
        var client = Client();

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync($"/r/l/{listingId}?s=tg")).StatusCode);

        Assert.Single(await factory.LinkClicksAsync(listingId));
    }

    [Theory]
    [InlineData(ListingStatus.Sold)]
    [InlineData(ListingStatus.Archived)]
    [InlineData(ListingStatus.PendingReview)]
    public async Task Inactive_listing_redirects_home_without_click(ListingStatus status)
    {
        var listingId = await SeedListingAsync($"inactive-{status}", status);

        var resp = await Client().GetAsync($"/r/l/{listingId}?s=tg");

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/", resp.Headers.Location!.AbsolutePath);
        Assert.Empty(await factory.LinkClicksAsync(listingId));
    }

    [Fact]
    public async Task Missing_listing_redirects_home()
    {
        var resp = await Client().GetAsync($"/r/l/{Guid.NewGuid()}?s=tg");

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/", resp.Headers.Location!.AbsolutePath);
    }

    [Fact]
    public async Task Failed_click_insert_still_redirects_to_card()
    {
        var listingId = await SeedListingAsync("insert-fails");
        var slug = await factory.ListingSlugAsync(listingId);

        // Таблица журнала «пропала» — вставка падает, чтение объявления работает.
        await factory.ExecuteSqlAsync("ALTER TABLE link_clicks RENAME TO link_clicks_offline");
        try
        {
            var resp = await Client().GetAsync($"/r/l/{listingId}?s=tg");

            Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
            Assert.Equal($"/listing/{slug}", resp.Headers.Location!.AbsolutePath);
        }
        finally
        {
            await factory.ExecuteSqlAsync("ALTER TABLE link_clicks_offline RENAME TO link_clicks");
        }
    }

    private async Task<Guid> SeedListingAsync(string marker, ListingStatus status = ListingStatus.Active)
    {
        var ownerId = await factory.SeedUserAsync($"redirect-{marker}-{Guid.NewGuid():N}@test.local", "CorrectHorse7");
        return await factory.SeedListingAsync(ownerId, status);
    }

    private HttpClient Client() =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
}

/// <summary>
/// Редирект без Docker: белый список источников, работа при недоступной БД и обход
/// глобального rate-limit. БД намеренно указывает в закрытый порт.
/// </summary>
public class LinkRedirectResilienceTests
{
    [Theory]
    [InlineData("tg", "tg")]
    [InlineData("TG", "tg")]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData(" tg", "unknown")]
    [InlineData("vk", "unknown")]
    [InlineData("<script>", "unknown")]
    public void Source_is_normalized_to_whitelist(string? raw, string expected) =>
        Assert.Equal(expected, LinkSources.Normalize(raw));

    [Fact]
    public void Journal_hash_never_falls_back_to_raw_ip()
    {
        var hasher = new HmacIpHasher(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        Assert.Equal(IpHasherExtensions.NoKeyHash, hasher.HashForJournal("203.0.113.7"));
    }

    /// <summary>
    /// Страж публичности: /r/l отвечает без токена на оба метода, которыми по ссылке ходят
    /// (GET — браузер, HEAD — curl -I и чекеры ссылок). Метод вне списка маршрута уходит
    /// в служебный endpoint «405» без AllowAnonymous, и FallbackPolicy отвечает 401 —
    /// именно так HEAD ломался на проде, пока GET работал.
    /// </summary>
    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task Redirect_answers_without_authorization(string method)
    {
        using var env = new EnvScope(
            ("ConnectionStrings__Postgres", "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2"),
            ("Telegram__SiteBaseUrl", "https://market.test/"));
        await using var app = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Development"));
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Контроль: FallbackPolicy включена и закрывает эндпоинт без [AllowAnonymous] —
        // без этого 302 ниже ничего бы не доказывал.
        var closed = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/api/me/favorites"));
        Assert.Equal(HttpStatusCode.Unauthorized, closed.StatusCode);

        var resp = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), $"/r/l/{Guid.NewGuid()}?s=tg"));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Empty(resp.Headers.WwwAuthenticate);
        Assert.Equal(new Uri("https://market.test/"), resp.Headers.Location);
    }

    [Fact]
    public async Task Redirect_bypasses_global_rate_limit_and_survives_database_outage()
    {
        using var env = new EnvScope(
            ("RateLimit__GlobalPerMinute", "2"),
            ("ConnectionStrings__Postgres", "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2"),
            ("Telegram__SiteBaseUrl", "https://market.test/"),
            // Пустой токен: проба ниже отвечает 503 и не ходит в сеть.
            ("Telegram__BotToken", ""));
        await using var app = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Development"));
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Контроль: глобальный лимит 2/мин действительно включён и уже исчерпан. Проба —
        // анонимный эндпоинт без БД и без DisableRateLimiting: на закрытый путь запрос получил бы
        // 401 от авторизации раньше, чем дошёл бы до лимитера (UseAuthorization стоит перед ним).
        const string probe = GenesisMarket.Api.Telegram.TelegramDiagnosticsEndpoints.Route;
        await client.GetAsync(probe);
        await client.GetAsync(probe);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync(probe)).StatusCode);

        // Редирект лимит не трогает, а без БД ведёт на главную вместо 500.
        for (var i = 0; i < 5; i++)
        {
            var resp = await client.GetAsync($"/r/l/{Guid.NewGuid()}?s=tg");
            Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
            Assert.Equal(new Uri("https://market.test/"), resp.Headers.Location);
        }
    }
}
