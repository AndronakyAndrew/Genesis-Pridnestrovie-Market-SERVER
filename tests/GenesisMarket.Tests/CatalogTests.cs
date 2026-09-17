using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Domain.Enums;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Каталог: только Active, курсорная пагинация, сортировки из белого списка,
/// валидация фильтров, count с кэшем. Тесты изолированы уникальной категорией:
/// БД в рамках класса общая, поэтому каждый тест фильтрует по своей категории.
/// </summary>
public class CatalogTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    [Fact]
    public async Task Only_active_listings_appear_even_for_owner()
    {
        var ownerEmail = Unique("cat-active");
        var owner = await factory.SeedUserAsync(ownerEmail, Password);
        const Category cat = Category.Transport;

        await factory.SeedListingAsync(owner, ListingStatus.Active, category: cat);
        await factory.SeedListingAsync(owner, ListingStatus.Sold, category: cat);
        await factory.SeedListingAsync(owner, ListingStatus.PendingReview, category: cat);
        await factory.SeedListingAsync(owner, ListingStatus.Rejected, category: cat);
        await factory.SeedListingAsync(owner, ListingStatus.Archived, category: cat);
        await factory.SeedListingAsync(owner, ListingStatus.Draft, category: cat);

        // Даже сам владелец, будучи аутентифицированным, видит в каталоге только Active.
        var client = await AuthedClient(ownerEmail);
        var page = await client.GetFromJsonAsync<JsonElement>("/api/listings?category=transport&limit=50");

        Assert.Equal(1, page.GetProperty("items").GetArrayLength());
        Assert.False(page.GetProperty("hasMore").GetBoolean());

        var count = await client.GetFromJsonAsync<JsonElement>("/api/listings/count?category=transport");
        Assert.Equal(1, count.GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task Cursor_pagination_walks_all_items_without_gaps_or_dupes()
    {
        var owner = await factory.SeedUserAsync(Unique("cat-page"), Password);
        const Category cat = Category.Electronics;

        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
            ids.Add(await factory.SeedListingAsync(owner, category: cat, title: $"Товар каталога номер {i}"));

        var client = factory.CreateClient();
        var seen = new List<Guid>();
        string? cursor = null;

        for (var guard = 0; guard < 10; guard++)
        {
            var url = $"/api/listings?category=electronics&sort=new&limit=2";
            if (cursor is not null) url += $"&cursor={Uri.EscapeDataString(cursor)}";
            var page = await client.GetFromJsonAsync<JsonElement>(url);

            foreach (var item in page.GetProperty("items").EnumerateArray())
                seen.Add(item.GetProperty("id").GetGuid());

            if (!page.GetProperty("hasMore").GetBoolean())
            {
                Assert.True(page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null);
                break;
            }
            cursor = page.GetProperty("nextCursor").GetString();
        }

        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
        Assert.Equal(ids.OrderBy(x => x), seen.OrderBy(x => x));
        // sort=new ⇒ новейшие (последние засеянные) идут первыми.
        Assert.Equal(ids[^1], seen[0]);
    }

    [Fact]
    public async Task Price_asc_orders_ascending_with_negotiable_last()
    {
        var owner = await factory.SeedUserAsync(Unique("cat-priceasc"), Password);
        const Category cat = Category.Fashion;

        await factory.SeedListingAsync(owner, category: cat, price: 300);
        await factory.SeedListingAsync(owner, category: cat, price: 100);
        await factory.SeedListingAsync(owner, category: cat, price: 200);
        await factory.SeedListingAsync(owner, category: cat, price: null, priceType: PriceType.Negotiable);

        var client = factory.CreateClient();
        var page = await client.GetFromJsonAsync<JsonElement>("/api/listings?category=fashion&sort=price_asc&limit=50");

        var prices = page.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("price").ValueKind == JsonValueKind.Null
                ? (decimal?)null
                : i.GetProperty("price").GetDecimal())
            .ToList();

        Assert.Equal(new decimal?[] { 100, 200, 300, null }, prices);
    }

    [Fact]
    public async Task Price_range_filter_excludes_out_of_band_and_negotiable()
    {
        var owner = await factory.SeedUserAsync(Unique("cat-priceband"), Password);
        const Category cat = Category.Home;

        await factory.SeedListingAsync(owner, category: cat, price: 100, subcategoryId: 18);
        var mid = await factory.SeedListingAsync(owner, category: cat, price: 200, subcategoryId: 18);
        await factory.SeedListingAsync(owner, category: cat, price: 300, subcategoryId: 18);
        await factory.SeedListingAsync(owner, category: cat, price: null, priceType: PriceType.Negotiable, subcategoryId: 18);

        var client = factory.CreateClient();
        var page = await client.GetFromJsonAsync<JsonElement>(
            "/api/listings?category=home&priceFrom=150&priceTo=250&limit=50");

        var items = page.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(mid, items[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Popular_orders_by_views_desc()
    {
        var owner = await factory.SeedUserAsync(Unique("cat-popular"), Password);
        const Category cat = Category.Work;

        var low = await factory.SeedListingAsync(owner, category: cat);
        var high = await factory.SeedListingAsync(owner, category: cat);
        var mid = await factory.SeedListingAsync(owner, category: cat);
        await factory.SetViewsAsync(low, 1);
        await factory.SetViewsAsync(high, 100);
        await factory.SetViewsAsync(mid, 10);

        var client = factory.CreateClient();
        var page = await client.GetFromJsonAsync<JsonElement>("/api/listings?category=work&sort=popular&limit=50");

        var order = page.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToList();

        Assert.Equal(new[] { high, mid, low }, order);
    }

    [Fact]
    public async Task Count_is_cached_and_matches_filter()
    {
        var owner = await factory.SeedUserAsync(Unique("cat-count"), Password);
        const Category cat = Category.Services;

        for (var i = 0; i < 3; i++)
            await factory.SeedListingAsync(owner, category: cat);

        var client = factory.CreateClient();
        var count = await client.GetFromJsonAsync<JsonElement>("/api/listings/count?category=services");
        Assert.Equal(3, count.GetProperty("count").GetInt64());

        // Ещё одно объявление, но в пределах окна кэша (60 c) count не меняется.
        await factory.SeedListingAsync(owner, category: cat);
        var cached = await client.GetFromJsonAsync<JsonElement>("/api/listings/count?category=services");
        Assert.Equal(3, cached.GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task Card_hides_owner_and_contacts()
    {
        var owner = await factory.SeedUserAsync(Unique("cat-card"), Password);
        await factory.SeedListingAsync(owner, category: Category.Other);

        var client = factory.CreateClient();
        var page = await client.GetFromJsonAsync<JsonElement>("/api/listings?category=other&limit=1");
        var item = page.GetProperty("items")[0];

        Assert.False(item.TryGetProperty("ownerId", out _));
        Assert.False(item.TryGetProperty("phone", out _));
        Assert.False(item.TryGetProperty("email", out _));
        Assert.False(item.TryGetProperty("description", out _));
        // Витринные поля присутствуют.
        Assert.True(item.TryGetProperty("slug", out _));
        Assert.True(item.TryGetProperty("firstImageUrl", out _));
        Assert.True(item.TryGetProperty("isBumped", out _));
        Assert.True(item.TryGetProperty("publishedAt", out _));
    }

    [Fact]
    public async Task Example_flag_is_exposed_and_example_is_never_shown_bumped()
    {
        var owner = await factory.SeedUserAsync(Unique("cat-example"), Password);
        var example = await factory.SeedListingAsync(owner, title: "Пример объявления кресло", isExample: true);
        var regular = await factory.SeedListingAsync(owner, title: "Настоящее кресло мягкое");

        // Оба «подняты по-настоящему»: BumpedAt позже и публикации, и одобрения.
        var bumpedAt = DateTimeOffset.UtcNow.AddSeconds(1);
        await factory.SetBumpTimestampsAsync(example, bumpedAt, bumpedAt.AddDays(-2));
        await factory.SetBumpTimestampsAsync(regular, bumpedAt, bumpedAt.AddDays(-2));

        var client = factory.CreateClient();
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/listings/{regular}");
        Assert.False(detail.GetProperty("isExample").GetBoolean());

        // Изоляция от соседних тестов — лента продавца (?seller=).
        var code = detail.GetProperty("ownerPublicCode").GetString();
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/listings?seller={code}&limit=50");
        var cards = page.GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("id").GetGuid());

        Assert.True(cards[example].GetProperty("isExample").GetBoolean());
        Assert.False(cards[example].GetProperty("isBumped").GetBoolean());
        Assert.False(cards[regular].GetProperty("isExample").GetBoolean());
        Assert.True(cards[regular].GetProperty("isBumped").GetBoolean());

        // Поиск (q) — та же карточка через FTS-проекцию.
        var search = await client.GetFromJsonAsync<JsonElement>(
            $"/api/listings?seller={code}&q={Uri.EscapeDataString("пример кресло")}");
        var found = Assert.Single(search.GetProperty("items").EnumerateArray());
        Assert.True(found.GetProperty("isExample").GetBoolean());
        Assert.False(found.GetProperty("isBumped").GetBoolean());

        // Избранное — отдельная проекция карточки.
        var buyerEmail = Unique("cat-example-buyer");
        await factory.SeedUserAsync(buyerEmail, Password);
        var buyer = await AuthedClient(buyerEmail);
        Assert.Equal(HttpStatusCode.OK, (await buyer.PostAsync($"/api/listings/{example}/favorite", null)).StatusCode);
        var favorites = await buyer.GetFromJsonAsync<JsonElement>("/api/me/favorites");
        Assert.True(favorites.GetProperty("items")[0].GetProperty("isExample").GetBoolean());
    }

    [Fact]
    public async Task Limit_above_max_is_clamped_not_rejected()
    {
        var owner = await factory.SeedUserAsync(Unique("cat-clamp"), Password);
        const Category cat = Category.Animals;
        for (var i = 0; i < 3; i++)
            await factory.SeedListingAsync(owner, category: cat);

        var client = factory.CreateClient();
        // limit=1000 > 50 — не ошибка, обрезается до максимума; тут вернётся всё (3 < 50).
        var resp = await client.GetAsync("/api/listings?category=animals&limit=1000");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var page = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, page.GetProperty("items").GetArrayLength());
    }

    [Theory]
    [InlineData("/api/listings?priceFrom=500&priceTo=100")]
    [InlineData("/api/listings?sort=bogus")]
    [InlineData("/api/listings?cities=tiraspol&cities=tiraspol&cities=tiraspol&cities=tiraspol&cities=tiraspol&cities=tiraspol&cities=tiraspol&cities=tiraspol")]
    [InlineData("/api/listings?sort=new&cursor=%21%21%21not-base64")]
    public async Task Invalid_filters_return_400(string url)
    {
        var client = factory.CreateClient();
        var resp = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
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
