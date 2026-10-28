using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Api.Accounts;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GenesisMarket.Tests;

public sealed class ShopPickupPointsTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    [Fact]
    public async Task Pickup_points_replace_list_update_checklist_and_public_profile()
    {
        var (ownerId, email, client) = await CreateShopAsync();

        var put = await client.PutAsJsonAsync("/api/shop/pickup-points", new
        {
            points = new[]
            {
                new { id = (Guid?)null, city = "Tiraspol", address = "ул. Ленина, 1", note = (string?)"Вход со двора", isMain = true, isActive = true, hours = (object?)null },
                new
                {
                    id = (Guid?)null, city = "Bendery", address = "ул. Пушкина, 2", note = (string?)null,
                    isMain = false, isActive = false,
                    hours = (object?)new
                    {
                        days = Enumerable.Range(1, 7).Select(day => new { day, closed = false, from = "00:00", to = "23:59" }).ToArray(),
                        breakFrom = (string?)null,
                        breakTo = (string?)null
                    }
                }
            },
            conditionsNote = "Забрать после звонка"
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using var saved = JsonDocument.Parse(await put.Content.ReadAsStringAsync());
        var points = saved.RootElement.GetProperty("points");
        Assert.Equal(2, points.GetArrayLength());
        Assert.True(points[0].GetProperty("isMain").GetBoolean());
        Assert.Null(points[0].GetProperty("openNow").GetBooleanOrNull());
        Assert.Equal(7, points[1].GetProperty("hours").GetProperty("days").GetArrayLength());
        Assert.True(points[1].GetProperty("openNow").GetBoolean());
        Assert.Equal("Забрать после звонка", saved.RootElement.GetProperty("conditionsNote").GetString());
        var mainId = points[0].GetProperty("id").GetGuid();

        using var me = JsonDocument.Parse(await (await client.GetAsync("/api/shop/me")).Content.ReadAsStringAsync());
        Assert.Equal(2, me.RootElement.GetProperty("pickupPointsCount").GetInt32());
        using var overview = JsonDocument.Parse(await (await client.GetAsync("/api/shop/overview")).Content.ReadAsStringAsync());
        Assert.True(overview.RootElement.GetProperty("checklist").EnumerateArray()
            .Single(x => x.GetProperty("key").GetString() == "pickup_address").GetProperty("done").GetBoolean());
        Assert.False(overview.RootElement.GetProperty("checklist").EnumerateArray()
            .Single(x => x.GetProperty("key").GetString() == "working_hours").GetProperty("done").GetBoolean());

        var linksResponse = await client.PutAsJsonAsync("/api/shop/links", new
        {
            instagram = "https://www.instagram.com/My.Shop_01/",
            website = "example.md/shop"
        });
        using var links = JsonDocument.Parse(await linksResponse.Content.ReadAsStringAsync());
        Assert.Equal("My.Shop_01", links.RootElement.GetProperty("instagram").GetString());
        Assert.Equal("https://example.md/shop", links.RootElement.GetProperty("website").GetString());

        var publicCode = await GetPublicCodeAsync(ownerId);
        using var publicProfile = JsonDocument.Parse(await (await factory.CreateClient()
            .GetAsync($"/api/users/by-code/{publicCode}/public")).Content.ReadAsStringAsync());
        var publicPickup = publicProfile.RootElement.GetProperty("pickup");
        Assert.Equal(1, publicPickup.GetProperty("points").GetArrayLength());
        Assert.Equal("Tiraspol", publicPickup.GetProperty("points")[0].GetProperty("city").GetString());
        Assert.Equal("My.Shop_01", publicProfile.RootElement.GetProperty("links").GetProperty("instagram").GetString());

        // PUT replaces the list, so the omitted inactive point is deleted. Omitted conditionsNote is preserved.
        var replace = await client.PutAsJsonAsync("/api/shop/pickup-points", new
        {
            points = new[]
            {
                new { id = mainId, city = "Tiraspol", address = "ул. Ленина, 1", note = "Обновлено", isMain = true, isActive = true, hours = (object?)null }
            }
        });
        Assert.Equal(HttpStatusCode.OK, replace.StatusCode);
        using var replaced = JsonDocument.Parse(await replace.Content.ReadAsStringAsync());
        Assert.Single(replaced.RootElement.GetProperty("points").EnumerateArray());
        Assert.Equal("Забрать после звонка", replaced.RootElement.GetProperty("conditionsNote").GetString());

        var clear = await client.PutAsJsonAsync("/api/shop/pickup-points", new { points = Array.Empty<object>(), conditionsNote = (string?)null });
        using var cleared = JsonDocument.Parse(await clear.Content.ReadAsStringAsync());
        Assert.Empty(cleared.RootElement.GetProperty("points").EnumerateArray());
        Assert.Null(cleared.RootElement.GetProperty("conditionsNote").GetStringOrNull());
        using var publicWithoutPoints = JsonDocument.Parse(await (await factory.CreateClient()
            .GetAsync($"/api/users/by-code/{publicCode}/public")).Content.ReadAsStringAsync());
        Assert.Null(publicWithoutPoints.RootElement.GetProperty("pickup").GetStringOrNull());
    }

    [Fact]
    public async Task Pickup_point_validation_returns_stable_codes_and_foreign_id_is_not_found()
    {
        var (ownerId, _, client) = await CreateShopAsync();
        var tooMany = await client.PutAsJsonAsync("/api/shop/pickup-points", new
        {
            points = Enumerable.Range(0, 6).Select(i => new
            {
                id = (Guid?)null, city = "Tiraspol", address = $"ул. {i}", note = (string?)null,
                isMain = i == 0, isActive = true, hours = (object?)null
            }).ToArray()
        });
        await AssertProblemCode(tooMany, "pickup_points_limit");

        var noMain = await client.PutAsJsonAsync("/api/shop/pickup-points", new
        {
            points = new[] { new { id = (Guid?)null, city = "Tiraspol", address = "Адрес 1", note = (string?)null, isMain = false, isActive = true, hours = (object?)null } }
        });
        await AssertProblemCode(noMain, "pickup_main_required");

        var inactiveMain = await client.PutAsJsonAsync("/api/shop/pickup-points", new
        {
            points = new[] { new { id = (Guid?)null, city = "Tiraspol", address = "Адрес 1", note = (string?)null, isMain = true, isActive = false, hours = (object?)null } }
        });
        await AssertProblemCode(inactiveMain, "pickup_main_inactive");

        var invalidHours = await client.PutAsJsonAsync("/api/shop/pickup-points", new
        {
            points = new[] { new
            {
                id = (Guid?)null, city = "Tiraspol", address = "Адрес 1", note = (string?)null, isMain = true, isActive = true,
                hours = new { days = Enumerable.Range(1, 7).Select(day => new { day, closed = false, from = "18:00", to = "09:00" }).ToArray(), breakFrom = (string?)null, breakTo = (string?)null }
            } }
        });
        await AssertProblemCode(invalidHours, "invalid_pickup_hours");

        var (otherId, _, otherClient) = await CreateShopAsync();
        var otherSave = await otherClient.PutAsJsonAsync("/api/shop/pickup-points", new
        {
            points = new[] { new { id = (Guid?)null, city = "Tiraspol", address = "Адрес чужой", note = (string?)null, isMain = true, isActive = true, hours = (object?)null } }
        });
        using var otherBody = JsonDocument.Parse(await otherSave.Content.ReadAsStringAsync());
        var foreignId = otherBody.RootElement.GetProperty("points")[0].GetProperty("id").GetGuid();
        var foreign = await client.PutAsJsonAsync("/api/shop/pickup-points", new
        {
            points = new[] { new { id = foreignId, city = "Tiraspol", address = "Адрес чужой", note = (string?)null, isMain = true, isActive = true, hours = (object?)null } }
        });
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(ownerId, await GetOwnerIdAsync(client));
        Assert.NotEqual(ownerId, otherId);
    }

    [Fact]
    public async Task Links_and_telegram_are_normalized_and_old_telegram_is_not_rechecked_when_omitted()
    {
        var (ownerId, email, client) = await CreateShopAsync();
        var invalidInstagram = await client.PutAsJsonAsync("/api/shop/links", new { instagram = "https://instagram.com/a b", website = (string?)null });
        await AssertProblemCode(invalidInstagram, "invalid_instagram");
        var invalidWebsite = await client.PutAsJsonAsync("/api/shop/links", new { instagram = (string?)null, website = "javascript:alert(1)" });
        await AssertProblemCode(invalidWebsite, "invalid_website");

        var normalized = await client.PatchAsJsonAsync("/api/me", new { telegramUsername = "@shop_user" });
        using var normalizedBody = JsonDocument.Parse(await normalized.Content.ReadAsStringAsync());
        Assert.Equal("shop_user", normalizedBody.RootElement.GetProperty("telegramUsername").GetString());
        var linkForm = await client.PatchAsJsonAsync("/api/me", new { telegramUsername = "t.me/shop_user" });
        using var linkBody = JsonDocument.Parse(await linkForm.Content.ReadAsStringAsync());
        Assert.Equal("shop_user", linkBody.RootElement.GetProperty("telegramUsername").GetString());

        var invalidTelegram = await client.PatchAsJsonAsync("/api/me", new { telegramUsername = "four" });
        await AssertProblemCode(invalidTelegram, "invalid_telegram_username");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await db.Profiles.SingleAsync(x => x.UserId == ownerId);
            profile.TelegramUsername = "legacy value kept as-is";
            await db.SaveChangesAsync();
        }

        var unrelatedPatch = await client.PatchAsJsonAsync("/api/me", new { displayName = "Новое имя" });
        using var unchanged = JsonDocument.Parse(await unrelatedPatch.Content.ReadAsStringAsync());
        Assert.Equal("legacy value kept as-is", unchanged.RootElement.GetProperty("telegramUsername").GetString());

        var publicCode = await GetPublicCodeAsync(ownerId);
        using var publicProfile = JsonDocument.Parse(await (await factory.CreateClient()
            .GetAsync($"/api/users/by-code/{publicCode}/public")).Content.ReadAsStringAsync());
        Assert.Null(publicProfile.RootElement.GetProperty("pickup").GetStringOrNull());
        Assert.Equal(JsonValueKind.Object, publicProfile.RootElement.GetProperty("links").ValueKind);
        Assert.Equal(email, await GetEmailAsync(ownerId));

        using var clearedLinks = JsonDocument.Parse(await (await client.PutAsJsonAsync("/api/shop/links",
            new { instagram = "", website = (string?)null })).Content.ReadAsStringAsync());
        Assert.Null(clearedLinks.RootElement.GetProperty("instagram").GetStringOrNull());
        Assert.Null(clearedLinks.RootElement.GetProperty("website").GetStringOrNull());
    }

    [Fact]
    public async Task Pickup_backfill_sql_is_idempotent_and_uses_profile_city_when_shop_city_is_null()
    {
        var userId = await factory.SeedUserAsync($"legacy-{Guid.NewGuid():N}@test.io", Password);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.BusinessProfiles.Add(BusinessProfile.Create(userId,
            new BusinessDetails("Старый магазин", LegalForm.IndividualEntrepreneur, "01-023-4567", "ул. Ленина, 5", null),
            DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(ShopPickupPointBackfillSql.Sql);
        await db.Database.ExecuteSqlRawAsync(ShopPickupPointBackfillSql.Sql);

        var point = await db.ShopPickupPoints.SingleAsync(x => x.OwnerId == userId);
        Assert.Equal("Tiraspol", point.City);
        Assert.Equal("ул. Ленина, 5", point.Address);
        Assert.True(point.IsMain);
        Assert.True(point.IsActive);
        Assert.Null(point.Hours);
    }

    [Fact]
    public async Task Account_anonymization_cascades_pickup_points_links_and_conditions()
    {
        var (ownerId, _, _) = await CreateShopAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var business = await db.BusinessProfiles.SingleAsync(x => x.UserId == ownerId);
            business.UpdatePublicLinks("shop_user", "https://example.md", DateTimeOffset.UtcNow);
            business.UpdateConditionsNote("Условия", DateTimeOffset.UtcNow);
            db.ShopPickupPoints.Add(new ShopPickupPoint
            {
                OwnerId = ownerId, City = "Tiraspol", Address = "ул. Ленина, 7", IsMain = true, IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var deletion = scope.ServiceProvider.GetRequiredService<IAccountDeletionService>();
            Assert.True(await deletion.DeleteAsync(ownerId, default));
        }

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await verifyDb.BusinessProfiles.AnyAsync(x => x.UserId == ownerId));
        Assert.False(await verifyDb.ShopPickupPoints.AnyAsync(x => x.OwnerId == ownerId));
    }

    private async Task<(Guid UserId, string Email, HttpClient Client)> CreateShopAsync()
    {
        var email = $"pickup-{Guid.NewGuid():N}@test.io";
        var userId = await factory.SeedUserAsync(email, Password);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BusinessProfiles.Add(BusinessProfile.CreateInvitedStore(userId, "Приглашённая лавка", "Tiraspol", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        var client = factory.CreateClient();
        var login = await factory.LoginAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("accessToken").GetString());
        return (userId, email, client);
    }

    private async Task<string> GetPublicCodeAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users.Where(x => x.Id == userId).Select(x => x.PublicCode).SingleAsync();
    }

    private async Task<string> GetEmailAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users.Where(x => x.Id == userId).Select(x => x.Email).SingleAsync();
    }

    private async Task<Guid> GetOwnerIdAsync(HttpClient client)
    {
        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
        return me.GetProperty("id").GetGuid();
    }

    private static async Task AssertProblemCode(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
    }
}

internal static class PickupJsonElementExtensions
{
    public static string? GetStringOrNull(this JsonElement element) =>
        element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : element.GetString();

    public static bool? GetBooleanOrNull(this JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : element.GetBoolean();
}
