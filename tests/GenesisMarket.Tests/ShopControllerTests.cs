using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GenesisMarket.Tests;

public sealed class ShopControllerTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    [Fact]
    public async Task Shop_routes_require_authentication_and_business_account()
    {
        var anonymous = await factory.CreateClient().GetAsync("/api/shop/me");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var email = Unique();
        await factory.SeedUserAsync(email, Password);
        var personal = await Login(email);
        var response = await personal.GetAsync("/api/shop/overview");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("business_account_required", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Invited_shop_without_organization_gets_me_overview_and_listing_contracts()
    {
        var email = Unique();
        var userId = await factory.SeedUserAsync(email, Password);
        await SeedInvitedShopAsync(userId);
        var active = await factory.SeedListingAsync(userId, ListingStatus.Active, "Активный тестовый товар", price: 300);
        var draft = await factory.SeedListingAsync(userId, ListingStatus.Draft, "Черновик тестовый товар", price: 100);
        await factory.SeedListingAsync(userId, ListingStatus.PendingReview, "Проверка тестовый товар", price: 200);
        await factory.SeedListingAsync(userId, ListingStatus.Sold, "Проданный тестовый товар", price: 400);
        await factory.SeedListingAsync(userId, ListingStatus.Archived, "Архивный тестовый товар", price: 500);
        await AddRevealEventsAsync(active, userId);

        var client = await Login(email);
        using var me = JsonDocument.Parse(await (await client.GetAsync("/api/shop/me")).Content.ReadAsStringAsync());
        Assert.Equal("false", me.RootElement.GetProperty("hasOrganization").GetRawText());
        Assert.True(me.RootElement.GetProperty("isInvitedStore").GetBoolean());
        Assert.Equal(5, me.RootElement.GetProperty("listingsCount").GetInt32());
        Assert.Null(me.RootElement.GetProperty("ratingAverage").GetDoubleOrNull());
        Assert.Null(me.RootElement.GetProperty("reviewsCount").GetInt32OrNull());

        using var overview = JsonDocument.Parse(await (await client.GetAsync("/api/shop/overview")).Content.ReadAsStringAsync());
        var counts = overview.RootElement.GetProperty("counts");
        Assert.Equal(5, counts.GetProperty("all").GetInt32());
        Assert.Equal(1, counts.GetProperty("draft").GetInt32());
        Assert.Equal(1, counts.GetProperty("review").GetInt32());
        Assert.Equal(1, counts.GetProperty("active").GetInt32());
        Assert.Equal(1, counts.GetProperty("sold").GetInt32());
        Assert.Equal(1, counts.GetProperty("archived").GetInt32());
        Assert.Equal(3, overview.RootElement.GetProperty("withoutPhotoCount").GetInt32());
        Assert.Equal(2, overview.RootElement.GetProperty("contactReveals").GetProperty("total").GetInt32());
        var hours = overview.RootElement.GetProperty("checklist").EnumerateArray()
            .Single(x => x.GetProperty("key").GetString() == "working_hours");
        Assert.Equal(JsonValueKind.Null, hours.GetProperty("done").ValueKind);

        using var all = JsonDocument.Parse(await (await client.GetAsync("/api/shop/listings?sort=price_asc&page=1&pageSize=2"))
            .Content.ReadAsStringAsync());
        Assert.Equal(5, all.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(2, all.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(draft, all.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal("draft", all.RootElement.GetProperty("items")[0].GetProperty("status").GetString());

        using var review = JsonDocument.Parse(await (await client.GetAsync("/api/shop/listings?status=review&q=ПРОВЕРКА"))
            .Content.ReadAsStringAsync());
        Assert.Equal(1, review.RootElement.GetProperty("total").GetInt32());
        Assert.Equal("review", review.RootElement.GetProperty("items")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Bulk_actions_dedupe_ids_report_owner_scoped_reasons_and_soft_delete()
    {
        var ownerEmail = Unique();
        var ownerId = await factory.SeedUserAsync(ownerEmail, Password);
        await SeedInvitedShopAsync(ownerId);
        var draft = await factory.SeedListingAsync(ownerId, ListingStatus.Draft, "Черновик для публикации");
        var cooldownListing = await factory.SeedListingAsync(ownerId, ListingStatus.Active, "Активный для cooldown");
        var archiveListing = await factory.SeedListingAsync(ownerId, ListingStatus.Active, "Активный для архива");
        var deleteListing = await factory.SeedListingAsync(ownerId, ListingStatus.Active, "Активный для удаления");

        var otherId = await factory.SeedUserAsync(Unique(), Password);
        var foreignListing = await factory.SeedListingAsync(otherId, ListingStatus.Active, "Чужое объявление");
        await factory.SetBumpTimestampsAsync(cooldownListing, DateTimeOffset.UtcNow);

        var client = await Login(ownerEmail);
        var publish = await client.PostAsJsonAsync("/api/shop/listings/bulk-publish", new { ids = new[] { draft, draft } });
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        using var published = JsonDocument.Parse(await publish.Content.ReadAsStringAsync());
        Assert.Single(published.RootElement.GetProperty("items").EnumerateArray());
        var publishItem = published.RootElement.GetProperty("items")[0];
        Assert.True(publishItem.GetProperty("ok").GetBoolean());
        Assert.True(publishItem.GetProperty("reason").ValueKind == JsonValueKind.Null
            || publishItem.GetProperty("reason").GetString() == "moderation_required");
        Assert.Contains(publishItem.GetProperty("status").GetString(), new[] { "review", "active" });

        var bump = await client.PostAsJsonAsync("/api/shop/listings/bulk-bump", new { ids = new[] { cooldownListing } });
        using var bumped = JsonDocument.Parse(await bump.Content.ReadAsStringAsync());
        var bumpItem = bumped.RootElement.GetProperty("items")[0];
        Assert.False(bumpItem.GetProperty("ok").GetBoolean());
        Assert.Equal("bump_cooldown", bumpItem.GetProperty("reason").GetString());
        Assert.True(bumpItem.GetProperty("retryAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);

        var archive = await client.PostAsJsonAsync("/api/shop/listings/bulk-archive", new { ids = new[] { archiveListing } });
        using var archived = JsonDocument.Parse(await archive.Content.ReadAsStringAsync());
        Assert.Equal("archived", archived.RootElement.GetProperty("items")[0].GetProperty("status").GetString());

        var delete = await client.PostAsJsonAsync("/api/shop/listings/bulk-delete",
            new { ids = new[] { deleteListing, foreignListing } });
        using var deleted = JsonDocument.Parse(await delete.Content.ReadAsStringAsync());
        var deletedItems = deleted.RootElement.GetProperty("items");
        Assert.Equal(2, deletedItems.GetArrayLength());
        var own = deletedItems.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == deleteListing);
        var foreign = deletedItems.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == foreignListing);
        Assert.True(own.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, own.GetProperty("status").ValueKind);
        Assert.Equal("not_found", foreign.GetProperty("reason").GetString());
        var missingId = Guid.NewGuid();
        var missing = await client.PostAsJsonAsync("/api/shop/listings/bulk-delete", new { ids = new[] { missingId } });
        using var missingBody = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
        Assert.Equal(foreign.GetProperty("reason").GetString(),
            missingBody.RootElement.GetProperty("items")[0].GetProperty("reason").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var deletedAt = await db.Listings.IgnoreQueryFilters().Where(x => x.Id == deleteListing)
            .Select(x => x.DeletedAt).SingleAsync();
        Assert.NotNull(deletedAt);

        var empty = await client.PostAsJsonAsync("/api/shop/listings/bulk-archive", new { ids = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        var tooMany = await client.PostAsJsonAsync("/api/shop/listings/bulk-archive", new { ids = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()) });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    [Fact]
    public async Task Bulk_publish_keeps_post_moderation_status_active()
    {
        var email = Unique();
        var userId = await factory.SeedUserAsync(email, Password, emailVerified: true);
        await SeedInvitedShopAsync(userId);
        await factory.SetAuthorTrustAsync(userId, approvedListings: 0, createdAt: DateTimeOffset.UtcNow.AddDays(-120));
        var draft = await factory.SeedListingAsync(userId, ListingStatus.Draft, "Спокойный товар для проверки");
        var client = await Login(email);

        var response = await client.PostAsJsonAsync("/api/shop/listings/bulk-publish", new { ids = new[] { draft } });
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = body.RootElement.GetProperty("items")[0];
        Assert.True(item.GetProperty("ok").GetBoolean());
        Assert.Equal("moderation_required", item.GetProperty("reason").GetString());
        Assert.Equal("active", item.GetProperty("status").GetString());
        var state = await factory.ListingModerationStateAsync(draft);
        Assert.Equal(ListingStatus.Active, state.Status);
        Assert.NotNull(state.ReviewQueuedAt);
    }

    private async Task SeedInvitedShopAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.BusinessProfiles.Add(BusinessProfile.CreateInvitedStore(userId, "Приглашённая лавка", "Tiraspol", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    private async Task AddRevealEventsAsync(Guid listingId, Guid viewerId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ContactReveals.AddRange(
            new ContactReveal { ListingId = listingId, ViewerUserId = viewerId, IpHash = new string('a', 64) },
            new ContactReveal { ListingId = listingId, ViewerUserId = viewerId, IpHash = new string('a', 64) });
        await db.SaveChangesAsync();
    }

    private async Task<HttpClient> Login(string email)
    {
        var client = factory.CreateClient();
        var response = await factory.LoginAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", doc.RootElement.GetProperty("accessToken").GetString());
        return client;
    }

    private static string Unique() => $"shop-{Guid.NewGuid():N}@test.io";
}

internal static class ShopJsonElementExtensions
{
    public static int? GetInt32OrNull(this JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : element.GetInt32();

    public static double? GetDoubleOrNull(this JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : element.GetDouble();
}
