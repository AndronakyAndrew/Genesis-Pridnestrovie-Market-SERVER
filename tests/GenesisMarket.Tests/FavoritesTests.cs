using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Domain.Enums;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Избранное: идемпотентный POST (составной PK), запрет на своё объявление,
/// денормализованный счётчик (триггер БД), isFavorite одним запросом на страницу,
/// isUnavailable для архивных, лимит 500. БД в классе общая — изоляция по владельцам.
/// </summary>
public class FavoritesTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    [Fact]
    public async Task Add_is_idempotent_and_does_not_duplicate()
    {
        var seller = await factory.SeedUserAsync(Unique("fav-idem-seller"), Password);
        var listing = await factory.SeedListingAsync(seller, category: Category.Transport);
        var buyer = await AuthedClient(Unique("fav-idem-buyer"));

        var first = await buyer.PostAsync($"/api/listings/{listing}/favorite", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Повтор — тоже 200, без 409 и без дубля.
        var second = await buyer.PostAsync($"/api/listings/{listing}/favorite", null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("isFavorite").GetBoolean());
        Assert.Equal(1, body.GetProperty("favoritesCount").GetInt32());

        // Счётчик денормализован и не задвоился.
        Assert.Equal(1, await factory.FavoritesCountAsync(listing));

        // В списке — ровно одна запись.
        var page = await buyer.GetFromJsonAsync<JsonElement>("/api/me/favorites");
        Assert.Equal(1, page.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Cannot_favorite_own_listing()
    {
        var ownerEmail = Unique("fav-own");
        var owner = await factory.SeedUserAsync(ownerEmail, Password);
        var listing = await factory.SeedListingAsync(owner, category: Category.Electronics);
        var client = await AuthedClient(ownerEmail, owner);

        var resp = await client.PostAsync($"/api/listings/{listing}/favorite", null);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(0, await factory.FavoritesCountAsync(listing));
    }

    [Fact]
    public async Task Add_missing_listing_returns_404()
    {
        var client = await AuthedClient(Unique("fav-404"));
        var resp = await client.PostAsync($"/api/listings/{Guid.NewGuid()}/favorite", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Remove_decrements_counter_and_is_idempotent()
    {
        var seller = await factory.SeedUserAsync(Unique("fav-remove-seller"), Password);
        var listing = await factory.SeedListingAsync(seller, category: Category.Fashion);
        var buyer = await AuthedClient(Unique("fav-remove-buyer"));

        await buyer.PostAsync($"/api/listings/{listing}/favorite", null);
        Assert.Equal(1, await factory.FavoritesCountAsync(listing));

        var del = await buyer.DeleteAsync($"/api/listings/{listing}/favorite");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        Assert.Equal(0, await factory.FavoritesCountAsync(listing));

        // Повторное удаление — тоже 204, счётчик не уходит в минус.
        var again = await buyer.DeleteAsync($"/api/listings/{listing}/favorite");
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(0, await factory.FavoritesCountAsync(listing));

        var page = await buyer.GetFromJsonAsync<JsonElement>("/api/me/favorites");
        Assert.Equal(0, page.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Catalog_card_reflects_isFavorite_and_count_for_current_user()
    {
        var seller = await factory.SeedUserAsync(Unique("fav-card-seller"), Password);
        const Category cat = Category.Kids;
        var favored = await factory.SeedListingAsync(seller, category: cat);
        var plain = await factory.SeedListingAsync(seller, category: cat);

        var buyerEmail = Unique("fav-card-buyer");
        var buyer = await AuthedClient(buyerEmail);
        await buyer.PostAsync($"/api/listings/{favored}/favorite", null);

        var page = await buyer.GetFromJsonAsync<JsonElement>($"/api/listings?category=kids&limit=50");
        var byId = page.GetProperty("items").EnumerateArray().ToDictionary(
            i => i.GetProperty("id").GetGuid(), i => i);

        Assert.True(byId[favored].GetProperty("isFavorite").GetBoolean());
        Assert.Equal(1, byId[favored].GetProperty("favoritesCount").GetInt32());
        Assert.False(byId[plain].GetProperty("isFavorite").GetBoolean());

        // Аноним всегда получает isFavorite=false.
        var anon = factory.CreateClient();
        var anonPage = await anon.GetFromJsonAsync<JsonElement>($"/api/listings?category=kids&limit=50");
        foreach (var item in anonPage.GetProperty("items").EnumerateArray())
            Assert.False(item.GetProperty("isFavorite").GetBoolean());
    }

    [Fact]
    public async Task Archived_listing_stays_favorited_but_marked_unavailable()
    {
        var seller = await factory.SeedUserAsync(Unique("fav-arch-seller"), Password);
        var listing = await factory.SeedListingAsync(seller, category: Category.Animals);
        var buyer = await AuthedClient(Unique("fav-arch-buyer"));

        await buyer.PostAsync($"/api/listings/{listing}/favorite", null);
        await factory.SetStatusAsync(listing, ListingStatus.Archived);

        var page = await buyer.GetFromJsonAsync<JsonElement>("/api/me/favorites");
        var item = page.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetGuid() == listing);

        // Не исчезает молча — остаётся с флагом недоступности.
        Assert.True(item.GetProperty("isUnavailable").GetBoolean());
        Assert.True(item.GetProperty("isFavorite").GetBoolean());
    }

    [Fact]
    public async Task Favorites_are_capped_at_500_per_user()
    {
        var buyerEmail = Unique("fav-limit-buyer");
        var buyer = await factory.SeedUserAsync(buyerEmail, Password);
        var seller = await factory.SeedUserAsync(Unique("fav-limit-seller"), Password);

        await factory.SeedManyFavoritesAsync(buyer, seller, 500);

        var client = await AuthedClient(buyerEmail, buyer);
        var extra = await factory.SeedListingAsync(seller, category: Category.Services);
        var resp = await client.PostAsync($"/api/listings/{extra}/favorite", null);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal(0, await factory.FavoritesCountAsync(extra));
    }

    [Fact]
    public async Task My_favorites_pagination_walks_all_without_gaps()
    {
        var seller = await factory.SeedUserAsync(Unique("fav-page-seller"), Password);
        var buyerEmail = Unique("fav-page-buyer");
        var buyer = await AuthedClient(buyerEmail);

        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var listing = await factory.SeedListingAsync(seller, category: Category.Home, title: $"Избранный товар {i}");
            var resp = await buyer.PostAsync($"/api/listings/{listing}/favorite", null);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            ids.Add(listing);
        }

        var seen = new List<Guid>();
        string? cursor = null;
        for (var guard = 0; guard < 10; guard++)
        {
            var url = "/api/me/favorites?limit=2";
            if (cursor is not null) url += $"&cursor={Uri.EscapeDataString(cursor)}";
            var page = await buyer.GetFromJsonAsync<JsonElement>(url);

            foreach (var item in page.GetProperty("items").EnumerateArray())
                seen.Add(item.GetProperty("id").GetGuid());

            if (!page.GetProperty("hasMore").GetBoolean())
                break;
            cursor = page.GetProperty("nextCursor").GetString();
        }

        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
        Assert.Equal(ids.OrderBy(x => x), seen.OrderBy(x => x));
        // Свежие сверху: последний добавленный — первый в выдаче.
        Assert.Equal(ids[^1], seen[0]);
    }

    [Fact]
    public async Task Favorites_endpoints_require_auth()
    {
        var anon = factory.CreateClient();
        var listing = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsync($"/api/listings/{listing}/favorite", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.DeleteAsync($"/api/listings/{listing}/favorite")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync("/api/me/favorites")).StatusCode);
    }

    // ---- helpers ----

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";

    private async Task<HttpClient> AuthedClient(string email, Guid? existing = null)
    {
        if (existing is null)
            await factory.SeedUserAsync(email, Password);

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }
}
