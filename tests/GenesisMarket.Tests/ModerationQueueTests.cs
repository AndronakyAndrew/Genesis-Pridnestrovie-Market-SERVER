using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Очередь объявлений рабочего места: возврат на доработку (без штрафа доверия),
/// вкладки и фильтры, назначение на себя, пакетные решения, рыночная медиана
/// в карточке и расширенная статистика дашборда.
/// </summary>
public class ModerationQueueTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    // ---- на доработку ----

    [Fact]
    public async Task Revise_returns_draft_with_reason_without_trust_penalty()
    {
        var (mod, _) = await Moderator();
        var sellerEmail = Unique("seller");
        var sellerId = await factory.SeedUserAsync(sellerEmail, Password);
        var listingId = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview);

        var resp = await mod.PostAsJsonAsync($"/api/moderation/listings/{listingId}/revise",
            new { reason = "BadPhotos", comment = "Нужно фото без водяных знаков" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var state = await factory.ListingModerationStateAsync(listingId);
        Assert.Equal(ListingStatus.Draft, state.Status);
        Assert.Null(state.ReviewQueuedAt);

        // Главное отличие от отказа: автору не фиксируется LastRejectedAt.
        Assert.Null((await factory.AuthorTrustAsync(sellerId)).LastRejectedAt);
        Assert.Equal(1, await factory.OutboxCountAsync(OutboxMessage.ListingRevisionRequested, listingId));
        Assert.Equal(0, await factory.OutboxCountAsync(OutboxMessage.ListingRejected, listingId));
        Assert.Equal(1, await factory.ModerationLogCountAsync(listingId, "listing.revise"));

        // Автор видит причину и отметку доработки в своём объявлении.
        var owner = await AuthedClient(sellerEmail);
        using (var doc = await GetJson(owner, $"/api/listings/{listingId}"))
        {
            Assert.Equal("BadPhotos", doc.RootElement.GetProperty("rejectionReasonCode").GetString());
            Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("revisionRequestedAt").ValueKind);
        }

        // Повторная публикация снимает причину и отметку.
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/listings/{listingId}/publish", null)).StatusCode);
        using (var doc = await GetJson(mod, $"/api/moderation/listings/{listingId}"))
        {
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("revisionRequestedAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("rejectionReasonCode").ValueKind);
        }
    }

    [Fact]
    public async Task Revise_of_post_moderated_listing_takes_down_channel_post()
    {
        var (mod, _) = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var listingId = await factory.SeedListingAsync(sellerId, ListingStatus.Active);
        await factory.QueueForPostReviewAsync(listingId);

        var resp = await mod.PostAsJsonAsync($"/api/moderation/listings/{listingId}/revise",
            new { reason = "WrongCategory" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(ListingStatus.Draft, (await factory.ListingModerationStateAsync(listingId)).Status);
        Assert.Equal(1, await factory.OutboxCountAsync(OutboxMessage.ListingChannelUpdate, listingId));
    }

    [Fact]
    public async Task Revise_with_other_and_no_comment_is_400()
    {
        var (mod, _) = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var listingId = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview);

        var resp = await mod.PostAsJsonAsync($"/api/moderation/listings/{listingId}/revise", new { reason = "Other" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(ListingStatus.PendingReview, (await factory.ListingModerationStateAsync(listingId)).Status);
    }

    // ---- таблица очереди ----

    [Fact]
    public async Task Queue_tabs_filters_and_owner_row()
    {
        var (mod, _) = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var marker = Guid.NewGuid().ToString("N")[..10];
        var pending = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview,
            title: $"Очередь {marker}", category: Category.Home, city: City.Rybnitsa);
        var toRevise = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview, title: $"Доработка {marker}");
        await mod.PostAsJsonAsync($"/api/moderation/listings/{toRevise}/revise", new { reason = "Duplicate" });

        using (var doc = await GetJson(mod, $"/api/moderation/listings?tab=Review&q={marker}&city=Rybnitsa"))
        {
            var row = Assert.Single(Items(doc));
            Assert.Equal(pending, row.GetProperty("id").GetGuid());
            Assert.Equal(sellerId, row.GetProperty("owner").GetProperty("id").GetGuid());
            Assert.False(row.GetProperty("overdue").GetBoolean());
        }

        using (var doc = await GetJson(mod, $"/api/moderation/listings?tab=Revision&q={marker}"))
            Assert.Equal(toRevise, Assert.Single(Items(doc)).GetProperty("id").GetGuid());

        using (var doc = await GetJson(mod, $"/api/moderation/listings?tab=All&q={marker}"))
            Assert.Equal(2, Items(doc).Count);
    }

    [Fact]
    public async Task Assign_mine_filter_conflict_and_force()
    {
        var (mod1, mod1Id) = await Moderator();
        var (mod2, _) = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var listingId = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview);

        Assert.Equal(HttpStatusCode.OK, (await mod1.PostAsync($"/api/moderation/listings/{listingId}/assign", null)).StatusCode);

        using (var doc = await GetJson(mod1, $"/api/moderation/listings?mine=true&q={listingId}"))
            Assert.Equal(mod1Id, Assert.Single(Items(doc)).GetProperty("assignee").GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.Conflict, (await mod2.PostAsync($"/api/moderation/listings/{listingId}/assign", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mod2.PostAsync($"/api/moderation/listings/{listingId}/assign?force=true", null)).StatusCode);

        // Решение снимает назначение: объявление ушло из очереди.
        await mod2.PostAsync($"/api/moderation/listings/{listingId}/approve", null);
        using (var doc = await GetJson(mod2, $"/api/moderation/listings/{listingId}"))
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("assignee").ValueKind);
    }

    [Fact]
    public async Task Counts_reflect_tabs_and_mine()
    {
        var (mod, _) = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var mine = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview);
        await mod.PostAsync($"/api/moderation/listings/{mine}/assign", null);

        using var doc = await GetJson(mod, "/api/moderation/listings/counts");
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("mine").GetInt32());
        Assert.True(root.GetProperty("review").GetInt32() >= 1);
        Assert.Equal(
            root.GetProperty("review").GetInt32() + root.GetProperty("revision").GetInt32() + root.GetProperty("rejected").GetInt32(),
            root.GetProperty("all").GetInt32());
        Assert.Equal(240, root.GetProperty("reviewSlaMinutes").GetInt32());
    }

    // ---- пакеты ----

    [Fact]
    public async Task Bulk_approve_processes_queued_and_reports_the_rest()
    {
        var (mod, _) = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var a = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview);
        var b = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview);
        var notQueued = await factory.SeedListingAsync(sellerId, ListingStatus.Active);
        var missing = Guid.NewGuid();

        var resp = await mod.PostAsJsonAsync("/api/moderation/listings/bulk/approve",
            new { ids = new[] { a, b, notQueued, missing } });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        var ok = doc.RootElement.GetProperty("succeeded").EnumerateArray().Select(x => x.GetGuid()).ToList();
        var failed = doc.RootElement.GetProperty("failed").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToList();

        Assert.Equal([a, b], ok);
        Assert.Equal([notQueued, missing], failed);
        Assert.Equal(ListingStatus.Active, (await factory.ListingModerationStateAsync(a)).Status);
        Assert.Equal(1, await factory.ModerationLogCountAsync(b, "listing.approve"));
        Assert.Equal(1, await factory.OutboxCountAsync(OutboxMessage.ListingApproved, a));
    }

    [Fact]
    public async Task Bulk_reject_requires_comment_for_other_and_caps_size()
    {
        var (mod, _) = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var a = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview);

        var noComment = await mod.PostAsJsonAsync("/api/moderation/listings/bulk/reject",
            new { ids = new[] { a }, reason = "Other" });
        Assert.Equal(HttpStatusCode.BadRequest, noComment.StatusCode);

        var tooMany = await mod.PostAsJsonAsync("/api/moderation/listings/bulk/approve",
            new { ids = Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToArray() });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);

        var ok = await mod.PostAsJsonAsync("/api/moderation/listings/bulk/reject",
            new { ids = new[] { a }, reason = "Duplicate" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(ListingStatus.Rejected, (await factory.ListingModerationStateAsync(a)).Status);
    }

    // ---- карточка и дашборд ----

    [Fact]
    public async Task Card_shows_market_median_only_with_enough_sample()
    {
        var (mod, _) = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var sub = await SubcategoryOf(mod, Category.Electronics);

        var target = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview,
            category: Category.Electronics, subcategoryId: sub, price: 900);

        foreach (var price in new[] { 1000m, 2000m, 3000m, 4000m })
            await factory.SeedListingAsync(sellerId, ListingStatus.Active,
                category: Category.Electronics, subcategoryId: sub, price: price);

        using (var doc = await GetJson(mod, $"/api/moderation/listings/{target}"))
        {
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("marketMedianPrice").ValueKind);
            Assert.Equal(4, doc.RootElement.GetProperty("marketSampleSize").GetInt32());
        }

        await factory.SeedListingAsync(sellerId, ListingStatus.Active,
            category: Category.Electronics, subcategoryId: sub, price: 5000);

        using (var doc = await GetJson(mod, $"/api/moderation/listings/{target}"))
        {
            Assert.Equal(3000m, doc.RootElement.GetProperty("marketMedianPrice").GetDecimal());
            Assert.Equal(sellerId, doc.RootElement.GetProperty("owner").GetProperty("id").GetGuid());
        }
    }

    [Fact]
    public async Task Stats_include_dashboard_fields()
    {
        var (mod, _) = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var listingId = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview);
        await mod.PostAsync($"/api/moderation/listings/{listingId}/approve", null);

        using var doc = await GetJson(mod, "/api/moderation/stats");
        var root = doc.RootElement;

        Assert.True(root.GetProperty("approvalsToday").GetInt32() >= 1);
        Assert.Equal(12, root.GetProperty("decisionsLast12h").GetArrayLength());
        Assert.True(root.GetProperty("decisionsLast12h").EnumerateArray().Sum(x => x.GetInt32()) >= 1);
        Assert.Equal(JsonValueKind.Number, root.GetProperty("slaPercentToday").ValueKind);
        Assert.Equal(240, root.GetProperty("reviewSlaMinutes").GetInt32());
    }

    // ---- helpers ----

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";

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

    /// <summary>Подкатегория, в которой больше никто в этом классе тестов не сидит.</summary>
    private static async Task<int> SubcategoryOf(HttpClient client, Category category)
    {
        using var doc = await GetJson(client, "/api/subcategories");
        return doc.RootElement.EnumerateArray()
            .Where(s => s.GetProperty("category").GetString() == category.ToString())
            .Select(s => s.GetProperty("id").GetInt32())
            .Last();
    }

    private static async Task<JsonDocument> GetJson(HttpClient client, string url)
    {
        var resp = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    }

    private static List<JsonElement> Items(JsonDocument doc) =>
        doc.RootElement.GetProperty("items").EnumerateArray().ToList();
}
