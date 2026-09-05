using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using GenesisMarket.Domain.Enums;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// SEO-подготовка бэкенда: мета карточки (title/description/og/JSON-LD), корректные HTTP-коды
/// (410/404/200+noindex), sitemap.xml, robots.txt, посадочные, канонический URL в DTO.
/// БД общая на класс — тесты изолируются уникальной категорией/городом/владельцем.
/// </summary>
public class SeoTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    [Fact]
    public async Task Meta_for_active_listing_has_canonical_og_and_product_jsonld()
    {
        var owner = await factory.SeedUserAsync(Unique("seo-meta"), Password);
        var id = await factory.SeedListingAsync(
            owner, ListingStatus.Active, title: "Велосипед горный", category: Category.Transport,
            price: 4500, city: City.Tiraspol);
        await factory.SeedListingImageAsync(id);

        var client = factory.CreateClient();
        var meta = await client.GetFromJsonAsync<JsonElement>($"/api/listings/{id}/meta");

        Assert.Contains("Велосипед горный", meta.GetProperty("title").GetString());
        Assert.False(meta.GetProperty("noIndex").GetBoolean());
        Assert.False(meta.GetProperty("isArchived").GetBoolean());

        // canonical — по slug на /listing/, абсолютный от публичного адреса.
        var listing = await client.GetFromJsonAsync<JsonElement>($"/api/listings/{id}");
        var slug = listing.GetProperty("slug").GetString();
        Assert.Equal($"https://market.test/listing/{slug}", meta.GetProperty("canonicalUrl").GetString());

        // og:image — presigned-ссылка на первое фото (не null, раз фото засеяно).
        Assert.Equal(JsonValueKind.String, meta.GetProperty("ogImage").ValueKind);

        // JSON-LD schema.org/Product с offers: цена в RUP, наличие InStock.
        var jsonLd = meta.GetProperty("jsonLd");
        Assert.Equal("https://schema.org", jsonLd.GetProperty("@context").GetString());
        Assert.Equal("Product", jsonLd.GetProperty("@type").GetString());
        var offers = jsonLd.GetProperty("offers");
        Assert.Equal("RUP", offers.GetProperty("priceCurrency").GetString());
        Assert.Equal("https://schema.org/InStock", offers.GetProperty("availability").GetString());
        Assert.Equal(4500, offers.GetProperty("price").GetInt32());
        // Пояснение валюты — текстом в описании (RUB/MDL не подставляем).
        Assert.Contains("рублях ПМР", jsonLd.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Meta_negotiable_price_omits_price_field()
    {
        var owner = await factory.SeedUserAsync(Unique("seo-negot"), Password);
        var id = await factory.SeedListingAsync(
            owner, ListingStatus.Active, category: Category.Services,
            price: null, priceType: PriceType.Negotiable);

        var client = factory.CreateClient();
        var meta = await client.GetFromJsonAsync<JsonElement>($"/api/listings/{id}/meta");
        var offers = meta.GetProperty("jsonLd").GetProperty("offers");

        Assert.False(offers.TryGetProperty("price", out _));
        Assert.Equal("RUP", offers.GetProperty("priceCurrency").GetString());
    }

    [Fact]
    public async Task Meta_for_archived_is_200_with_noindex()
    {
        var owner = await factory.SeedUserAsync(Unique("seo-arch"), Password);
        var id = await factory.SeedListingAsync(owner, ListingStatus.Archived, category: Category.Kids);

        var client = factory.CreateClient();
        var response = await client.GetAsync($"/api/listings/{id}/meta");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var meta = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(meta.GetProperty("isArchived").GetBoolean());
        Assert.True(meta.GetProperty("noIndex").GetBoolean());
        Assert.Equal("https://schema.org/Discontinued",
            meta.GetProperty("jsonLd").GetProperty("offers").GetProperty("availability").GetString());
    }

    [Fact]
    public async Task Meta_for_sold_is_200_soldout()
    {
        var owner = await factory.SeedUserAsync(Unique("seo-sold"), Password);
        var id = await factory.SeedListingAsync(owner, ListingStatus.Active, category: Category.Electronics);
        await factory.SetStatusAsync(id, ListingStatus.Sold);

        var client = factory.CreateClient();
        var meta = await client.GetFromJsonAsync<JsonElement>($"/api/listings/{id}/meta");

        Assert.True(meta.GetProperty("isArchived").GetBoolean());
        Assert.True(meta.GetProperty("noIndex").GetBoolean());
        Assert.Equal("https://schema.org/SoldOut",
            meta.GetProperty("jsonLd").GetProperty("offers").GetProperty("availability").GetString());
    }

    [Fact]
    public async Task Meta_for_deleted_listing_is_410_gone()
    {
        var owner = await factory.SeedUserAsync(Unique("seo-del"), Password);
        var id = await factory.SeedListingAsync(owner, ListingStatus.Active, category: Category.Fashion);
        await factory.SoftDeleteListingAsync(id);

        var client = factory.CreateClient();
        var response = await client.GetAsync($"/api/listings/{id}/meta");
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }

    [Fact]
    public async Task Meta_for_draft_and_missing_is_404()
    {
        var owner = await factory.SeedUserAsync(Unique("seo-draft"), Password);
        var draft = await factory.SeedListingAsync(owner, ListingStatus.Draft, category: Category.Animals);

        var client = factory.CreateClient();

        var draftResponse = await client.GetAsync($"/api/listings/{draft}/meta");
        Assert.Equal(HttpStatusCode.NotFound, draftResponse.StatusCode);

        var missingResponse = await client.GetAsync($"/api/listings/{Guid.NewGuid()}/meta");
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
    }

    [Fact]
    public async Task Listing_dto_always_carries_canonical_url()
    {
        var owner = await factory.SeedUserAsync(Unique("seo-canon"), Password);
        var id = await factory.SeedListingAsync(owner, ListingStatus.Active, category: Category.Home);

        var client = factory.CreateClient();
        var listing = await client.GetFromJsonAsync<JsonElement>($"/api/listings/{id}");
        var slug = listing.GetProperty("slug").GetString();

        Assert.Equal($"https://market.test/listing/{slug}", listing.GetProperty("canonicalUrl").GetString());
    }

    [Fact]
    public async Task Robots_closes_private_pages_and_apis_and_points_to_sitemap()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/robots.txt");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();

        // Приватные страницы сайта: кабинет, вход, создание/редактирование, модерация.
        Assert.Contains("Disallow: /create", body);
        Assert.Contains("Disallow: /profile", body);
        Assert.Contains("Disallow: /auth", body);
        Assert.Contains("Disallow: /admin", body);
        Assert.Contains("Disallow: /listing/new", body);
        Assert.Contains("Disallow: /login", body);
        Assert.Contains("Disallow: /moderation", body);

        // Служебные API.
        Assert.Contains("Disallow: /api/moderation/", body);
        Assert.Contains("Disallow: /api/me/", body);
        Assert.Contains("Disallow: /api/auth/", body);

        Assert.Contains("Sitemap: https://market.test/sitemap.xml", body);
    }

    [Fact]
    public async Task Sitemap_lists_static_pages_and_active_listing()
    {
        var owner = await factory.SeedUserAsync(Unique("seo-sitemap"), Password);
        var id = await factory.SeedListingAsync(owner, ListingStatus.Active, category: Category.Transport);
        var slug = await factory.ListingSlugAsync(id);

        var xml = await SitemapAsync();

        Assert.Contains("<urlset", xml);
        Assert.Contains("<loc>https://market.test/</loc>", xml);                   // главная
        Assert.Contains("<loc>https://market.test/catalog</loc>", xml);            // каталог
        Assert.Contains($"<loc>https://market.test/listing/{slug}</loc>", xml);    // карточка
    }

    [Fact]
    public async Task Sitemap_omits_sold_and_archived_listings()
    {
        var owner = await factory.SeedUserAsync(Unique("seo-sitemap-status"), Password);
        var active = await factory.SeedListingAsync(owner, ListingStatus.Active, category: Category.Electronics);
        var sold = await factory.SeedListingAsync(owner, ListingStatus.Active, category: Category.Electronics);
        await factory.SetStatusAsync(sold, ListingStatus.Sold);
        var archived = await factory.SeedListingAsync(owner, ListingStatus.Archived, category: Category.Electronics);

        var xml = await SitemapAsync();

        Assert.Contains(await factory.ListingSlugAsync(active), xml);
        Assert.DoesNotContain(await factory.ListingSlugAsync(sold), xml);
        Assert.DoesNotContain(await factory.ListingSlugAsync(archived), xml);
    }

    [Fact]
    public async Task Sitemap_does_not_list_private_pages()
    {
        var xml = await SitemapAsync();

        string[] privatePaths =
        [
            "/create", "/profile", "/auth", "/admin",
            "/listing/new", "/login", "/favorites", "/moderation", "/user/"
        ];
        foreach (var path in privatePaths)
            Assert.DoesNotContain($"<loc>https://market.test{path}", xml);
    }

    [Fact]
    public async Task Sitemap_is_valid_sitemap_protocol_document()
    {
        var owner = await factory.SeedUserAsync(Unique("seo-sitemap-xml"), Password);
        await factory.SeedListingAsync(owner, ListingStatus.Active, category: Category.Fashion);

        var doc = XDocument.Parse(await SitemapAsync());
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

        Assert.Equal(ns + "urlset", doc.Root!.Name);

        var urls = doc.Root.Elements(ns + "url").ToList();
        Assert.NotEmpty(urls);
        foreach (var url in urls)
        {
            Assert.False(string.IsNullOrWhiteSpace((string?)url.Element(ns + "loc")));
            Assert.Contains((string?)url.Element(ns + "changefreq"), new[] { "daily", "weekly", "monthly" });

            var priority = decimal.Parse(
                (string)url.Element(ns + "priority")!, CultureInfo.InvariantCulture);
            Assert.InRange(priority, 0m, 1m);

            // lastmod — ISO 8601 в UTC.
            if (url.Element(ns + "lastmod") is { } lastMod)
            {
                Assert.EndsWith("Z", lastMod.Value);
                var parsed = DateTimeOffset.Parse(
                    lastMod.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                Assert.Equal(TimeSpan.Zero, parsed.Offset);
            }
        }

        // У объявления дата изменения есть — значит хотя бы один lastmod в карте присутствует.
        Assert.Contains(urls, u => u.Element(ns + "lastmod") is not null);
    }

    [Fact]
    public async Task Sitemap_response_is_xml_utf8_and_cacheable()
    {
        factory.SitemapCache.Invalidate();
        var client = factory.CreateClient();
        var response = await client.GetAsync("/sitemap.xml");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Contains("max-age=3600", response.Headers.CacheControl?.ToString() ?? "");
    }

    [Fact]
    public async Task Sitemap_within_ttl_is_served_from_cache_without_touching_db()
    {
        var owner = await factory.SeedUserAsync(Unique("seo-sitemap-cache"), Password);
        var first = await factory.SeedListingAsync(owner, ListingStatus.Active, category: Category.Animals);

        // Первый обход: карта собрана из БД и уложена в кэш на TTL.
        Assert.Contains(await factory.ListingSlugAsync(first), await SitemapAsync());

        // Новое объявление внутри TTL в карте не появляется — значит второй запрос
        // отвечен из кэша и в БД не ходил.
        var second = await factory.SeedListingAsync(owner, ListingStatus.Active, category: Category.Animals);
        var cached = await SitemapAsync(invalidate: false);
        Assert.DoesNotContain(await factory.ListingSlugAsync(second), cached);

        // После сброса кэша (в жизни — по истечении TTL) объявление появляется.
        Assert.Contains(await factory.ListingSlugAsync(second), await SitemapAsync());
    }

    /// <summary>
    /// Карта сайта строкой. По умолчанию кэш сбрасывается: иначе тест видел бы ответ,
    /// собранный соседним тестом до его сидов.
    /// </summary>
    private async Task<string> SitemapAsync(bool invalidate = true)
    {
        if (invalidate)
            factory.SitemapCache.Invalidate();

        var client = factory.CreateClient();
        var response = await client.GetAsync("/sitemap.xml");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Landing_returns_count_price_range_and_top_subcategories()
    {
        var owner = await factory.SeedUserAsync(Unique("seo-landing"), Password);
        const Category cat = Category.Kids;
        const City city = City.Dnestrovsk; // редко используемая пара — изоляция от прочих тестов

        await factory.SeedListingAsync(owner, ListingStatus.Active, category: cat, city: city, price: 100, subcategoryId: 18);
        await factory.SeedListingAsync(owner, ListingStatus.Active, category: cat, city: city, price: 300, subcategoryId: 18);
        await factory.SeedListingAsync(owner, ListingStatus.Active, category: cat, city: city, price: null,
            priceType: PriceType.Negotiable, subcategoryId: 18);
        // Другой город той же категории — в сводку не попадает.
        await factory.SeedListingAsync(owner, ListingStatus.Active, category: cat, city: City.Rybnitsa, price: 999);

        var client = factory.CreateClient();
        var landing = await client.GetFromJsonAsync<JsonElement>("/api/seo/landing/kids/dnestrovsk");

        Assert.Equal("kids", landing.GetProperty("category").GetString());
        Assert.Equal("dnestrovsk", landing.GetProperty("city").GetString());
        Assert.Equal("Детские товары", landing.GetProperty("categoryLabel").GetString());
        Assert.Equal("Днестровск", landing.GetProperty("cityLabel").GetString());
        Assert.Equal(3, landing.GetProperty("count").GetInt64());
        Assert.Equal(100, landing.GetProperty("priceFrom").GetInt32());
        Assert.Equal(300, landing.GetProperty("priceTo").GetInt32());
        Assert.Equal("https://market.test/kids/dnestrovsk", landing.GetProperty("canonicalUrl").GetString());

        var top = landing.GetProperty("topSubcategories");
        Assert.True(top.GetArrayLength() >= 1);
        Assert.Equal(18, top[0].GetProperty("subcategoryId").GetInt32());
        Assert.Equal(3, top[0].GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task Landing_for_unknown_pair_is_404()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/seo/landing/nonsense/tiraspol");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";
}
