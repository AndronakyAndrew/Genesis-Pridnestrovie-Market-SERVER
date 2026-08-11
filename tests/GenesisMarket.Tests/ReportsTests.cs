using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Жалобы: анонимный приём, дедупликация повторов от одного репортёра, rate-limit
/// и автоматика — N независимых Fraud/Prohibited на объявление переводят его
/// в модерацию.
/// </summary>
public class ReportsTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    [Fact]
    public async Task Duplicate_report_from_same_reporter_returns_200_without_new_row()
    {
        var (_, listingId) = await SeedSellerWithListing();

        var reporter = Unique("reporter");
        await factory.SeedUserAsync(reporter, Password);
        var client = await AuthedClient(reporter);

        var first = await client.PostAsJsonAsync("/api/reports",
            new { targetType = "Listing", targetId = listingId, reason = "Spam", comment = "Спам" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/reports",
            new { targetType = "Listing", targetId = listingId, reason = "Spam", comment = "Ещё раз спам" });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        // Повтор не создал новую запись.
        Assert.Equal(1, await factory.ReportCountAsync(listingId));
    }

    [Fact]
    public async Task Three_independent_fraud_reports_flag_listing_for_moderation()
    {
        var (_, listingId) = await SeedSellerWithListing();

        Assert.Equal("Active", (await factory.ListingModerationAsync(listingId)).Status);

        // Три РАЗНЫХ пользователя жалуются на мошенничество.
        for (var i = 0; i < 3; i++)
        {
            var reporter = Unique($"fraud{i}");
            await factory.SeedUserAsync(reporter, Password);
            var client = await AuthedClient(reporter);
            var resp = await client.PostAsJsonAsync("/api/reports",
                new { targetType = "Listing", targetId = listingId, reason = "Fraud", comment = "Мошенник" });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        }

        // Порог достигнут: объявление ушло в PendingReview и в начало очереди.
        var (status, priority) = await factory.ListingModerationAsync(listingId);
        Assert.Equal("PendingReview", status);
        Assert.True(priority > 0);
    }

    [Fact]
    public async Task Two_fraud_reports_do_not_flag_listing()
    {
        var (_, listingId) = await SeedSellerWithListing();

        for (var i = 0; i < 2; i++)
        {
            var reporter = Unique($"twofraud{i}");
            await factory.SeedUserAsync(reporter, Password);
            var client = await AuthedClient(reporter);
            await client.PostAsJsonAsync("/api/reports",
                new { targetType = "Listing", targetId = listingId, reason = "Fraud", comment = "Подозрительно" });
        }

        // Ниже порога (2 < 3) — объявление не трогаем.
        Assert.Equal("Active", (await factory.ListingModerationAsync(listingId)).Status);
    }

    [Fact]
    public async Task Report_on_nonexistent_target_returns_404()
    {
        var reporter = Unique("reporter");
        await factory.SeedUserAsync(reporter, Password);
        var client = await AuthedClient(reporter);

        var resp = await client.PostAsJsonAsync("/api/reports",
            new { targetType = "Listing", targetId = Guid.NewGuid(), reason = "Spam", comment = (string?)null });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Anonymous_can_report_and_is_rate_limited()
    {
        var (_, listingId) = await SeedSellerWithListing();

        var client = factory.CreateClient(); // аноним: лимит 5/час на IpHash

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            var resp = await client.PostAsJsonAsync("/api/reports",
                new { targetType = "Listing", targetId = listingId, reason = "Fraud", comment = "Аноним заметил" });
            statuses.Add(resp.StatusCode);
        }

        // Первая жалоба принята; на 6-й срабатывает лимит.
        Assert.Equal(HttpStatusCode.Created, statuses[0]);
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[5]);

        var limited = await client.PostAsJsonAsync("/api/reports",
            new { targetType = "Listing", targetId = listingId, reason = "Fraud", comment = "И ещё" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
    }

    // ---- helpers ----

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";

    private async Task<(Guid SellerId, Guid ListingId)> SeedSellerWithListing()
    {
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var listingId = await factory.SeedListingAsync(sellerId);
        return (sellerId, listingId);
    }

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
