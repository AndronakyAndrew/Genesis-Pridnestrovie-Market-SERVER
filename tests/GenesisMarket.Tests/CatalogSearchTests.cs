using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Domain.Enums;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Полнотекстовый поиск: FTS (websearch_to_tsquery + ts_rank_cd), комбинирование
/// с фильтрами, keyset по релевантности, fuzzy-fallback (pg_trgm), лог промахов,
/// XSS-безопасная подсветка. Изоляция — уникальной категорией на тест.
/// </summary>
public class CatalogSearchTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    [Fact]
    public async Task Fts_matches_and_ranks_title_above_description()
    {
        var owner = await factory.SeedUserAsync(Unique("s-fts"), Password);
        const Category cat = Category.Electronics;

        var titleHit = await factory.SeedListingAsync(owner, category: cat,
            title: "Игровой ноутбук ASUS", description: "мощная машина для игр");
        var descHit = await factory.SeedListingAsync(owner, category: cat,
            title: "Сумка для техники", description: "удобно носить ноутбук и зарядку");
        await factory.SeedListingAsync(owner, category: cat,
            title: "Холодильник Атлант", description: "бытовая техника для кухни");

        var client = factory.CreateClient();
        var page = await client.GetFromJsonAsync<JsonElement>(
            "/api/listings?q=ноутбук&category=electronics&sort=relevance&limit=50");

        var ids = Ids(page);
        Assert.Equal(2, ids.Count);
        // Заголовок (вес A) релевантнее описания (вес B).
        Assert.Equal(titleHit, ids[0]);
        Assert.Equal(descHit, ids[1]);
    }

    [Fact]
    public async Task Special_characters_do_not_error_or_change_meaning()
    {
        var owner = await factory.SeedUserAsync(Unique("s-spec"), Password);
        const Category cat = Category.Transport;
        var target = await factory.SeedListingAsync(owner, category: cat,
            title: "Продам диван кожаный", description: "мягкий и удобный");

        var client = factory.CreateClient();

        var plain = await client.GetFromJsonAsync<JsonElement>(
            "/api/listings?q=диван&category=transport&limit=50");

        // Те же спецсимволы, что ломают to_tsquery: ' " & | ! : *
        var messyQ = Uri.EscapeDataString("диван' \" & | ! :*");
        var messyResp = await client.GetAsync($"/api/listings?q={messyQ}&category=transport&limit=50");
        Assert.Equal(HttpStatusCode.OK, messyResp.StatusCode);
        var messy = await messyResp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(new[] { target }, Ids(plain).ToArray());
        Assert.Equal(Ids(plain), Ids(messy)); // смысл не изменился

        // Запрос только из спецсимволов — не ошибка БД (просто пусто/fallback).
        var onlySpecial = Uri.EscapeDataString("' \" & | ! :*");
        var onlyResp = await client.GetAsync($"/api/listings?q={onlySpecial}&category=transport");
        Assert.Equal(HttpStatusCode.OK, onlyResp.StatusCode);
    }

    [Fact]
    public async Task Search_combines_with_filters()
    {
        var owner = await factory.SeedUserAsync(Unique("s-filter"), Password);
        const Category cat = Category.Fashion;

        var inTiraspol = await factory.SeedListingAsync(owner, category: cat,
            title: "Куртка зимняя пуховая", city: City.Tiraspol);
        await factory.SeedListingAsync(owner, category: cat,
            title: "Куртка зимняя пуховая", city: City.Bendery);

        var client = factory.CreateClient();
        var page = await client.GetFromJsonAsync<JsonElement>(
            "/api/listings?q=куртка&category=fashion&cities=tiraspol&limit=50");

        Assert.Equal(new[] { inTiraspol }, Ids(page).ToArray());
    }

    [Fact]
    public async Task Relevance_cursor_paginates_without_gaps_or_dupes()
    {
        var owner = await factory.SeedUserAsync(Unique("s-page"), Password);
        const Category cat = Category.Kids;

        var seeded = new List<Guid>();
        for (var i = 0; i < 5; i++)
            seeded.Add(await factory.SeedListingAsync(owner, category: cat,
                title: $"Коляска детская модель {i}", description: "прогулочная коляска"));

        var client = factory.CreateClient();
        var seen = new List<Guid>();
        string? cursor = null;

        for (var guard = 0; guard < 10; guard++)
        {
            var url = "/api/listings?q=коляска&category=kids&sort=relevance&limit=2";
            if (cursor is not null) url += $"&cursor={Uri.EscapeDataString(cursor)}";
            var page = await client.GetFromJsonAsync<JsonElement>(url);
            seen.AddRange(Ids(page));

            if (!page.GetProperty("hasMore").GetBoolean())
                break;
            cursor = page.GetProperty("nextCursor").GetString();
        }

        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
        Assert.Equal(seeded.OrderBy(x => x), seen.OrderBy(x => x));
    }

    [Fact]
    public async Task Typo_falls_back_to_trigram_similarity()
    {
        var owner = await factory.SeedUserAsync(Unique("s-typo"), Password);
        const Category cat = Category.RealEstate;
        var fridge = await factory.SeedListingAsync(owner, category: cat,
            title: "Холодильник", description: "бытовая техника");

        var client = factory.CreateClient();
        // Опечатка: о→а. FTS не найдёт лексему, сработает fuzzy similarity(Title, q) > 0.3.
        var page = await client.GetFromJsonAsync<JsonElement>(
            "/api/listings?q=халодильник&category=realestate&limit=50");

        Assert.Equal(new[] { fridge }, Ids(page).ToArray());
        // Fallback — первая и единственная страница.
        Assert.False(page.GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Zero_result_query_is_logged_to_search_misses()
    {
        var owner = await factory.SeedUserAsync(Unique("s-miss"), Password);
        const Category cat = Category.Animals;
        await factory.SeedListingAsync(owner, category: cat, title: "Попугай волнистый");

        const string gibberish = "цщксэёйзюнтъь"; // ни FTS, ни fuzzy не найдут
        var client = factory.CreateClient();
        var page = await client.GetFromJsonAsync<JsonElement>(
            $"/api/listings?q={Uri.EscapeDataString(gibberish)}&category=animals");

        Assert.Empty(page.GetProperty("items").EnumerateArray());
        Assert.False(page.GetProperty("hasMore").GetBoolean());
        Assert.Equal(1, await factory.SearchMissCountAsync(gibberish));
    }

    [Fact]
    public async Task Highlight_escapes_source_before_marking()
    {
        var owner = await factory.SeedUserAsync(Unique("s-xss"), Password);
        const Category cat = Category.Work;
        await factory.SeedListingAsync(owner, category: cat,
            title: "Ноутбук <script>alert(1)</script>", description: "продам срочно");

        var client = factory.CreateClient();
        var page = await client.GetFromJsonAsync<JsonElement>(
            "/api/listings?q=ноутбук&category=work&limit=1");

        var highlight = page.GetProperty("items")[0].GetProperty("titleHighlight").GetString()!;
        Assert.Contains("<mark>", highlight);            // совпадение подсвечено
        Assert.Contains("&lt;script&gt;", highlight);    // исходный HTML экранирован
        Assert.DoesNotContain("<script>", highlight);    // сырого тега нет → нет XSS
    }

    // ---- helpers ----

    private static List<Guid> Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid())
            .ToList();

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";
}
