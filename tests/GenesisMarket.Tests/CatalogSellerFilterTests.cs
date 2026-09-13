using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Domain.Enums;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Фильтр каталога по продавцу (<c>seller</c> — «ID профиля»). Регрессия: до фильтра
/// параметр молча игнорировался, и публичный профиль любого продавца показывал
/// весь каталог.
/// </summary>
public class CatalogSellerFilterTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    [Fact]
    public async Task Seller_filter_returns_only_that_sellers_active_listings()
    {
        var sellerEmail = Unique("seller-a");
        var seller = await factory.SeedUserAsync(sellerEmail, Password);
        var other = await factory.SeedUserAsync(Unique("seller-b"), Password);

        var active1 = await factory.SeedListingAsync(seller, title: "Продам велосипед горный");
        var active2 = await factory.SeedListingAsync(seller, title: "Продам самокат детский");
        await factory.SeedListingAsync(seller, ListingStatus.Sold, title: "Продан ноутбук старый");
        await factory.SeedListingAsync(other, title: "Чужое объявление кресло");

        var code = await PublicCodeAsync(sellerEmail);
        var client = factory.CreateClient();

        var page = await client.GetFromJsonAsync<JsonElement>($"/api/listings?seller={code}&limit=50");
        var ids = page.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid())
            .ToHashSet();

        Assert.Equal(new HashSet<Guid> { active1, active2 }, ids);
        Assert.False(page.GetProperty("hasMore").GetBoolean());

        var count = await client.GetFromJsonAsync<JsonElement>($"/api/listings/count?seller={code}");
        Assert.Equal(2, count.GetProperty("count").GetInt64());
    }

    [Theory]
    [InlineData("00000")] // верная форма, но такого кода не выдаётся
    [InlineData("abc")]   // не та форма
    public async Task Unknown_seller_returns_empty_page_not_whole_catalog(string seller)
    {
        var owner = await factory.SeedUserAsync(Unique("seller-any"), Password);
        await factory.SeedListingAsync(owner, title: "Объявление в общем каталоге");

        var client = factory.CreateClient();

        var resp = await client.GetAsync($"/api/listings?seller={seller}&limit=50");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var page = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, page.GetProperty("items").GetArrayLength());
        Assert.False(page.GetProperty("hasMore").GetBoolean());

        var count = await client.GetFromJsonAsync<JsonElement>($"/api/listings/count?seller={seller}");
        Assert.Equal(0, count.GetProperty("count").GetInt64());
    }

    // ---- helpers ----

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";

    /// <summary>«ID профиля» пользователя — из его же /api/me.</summary>
    private async Task<string> PublicCodeAsync(string email)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", doc.RootElement.GetProperty("accessToken").GetString());

        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
        return me.GetProperty("publicCode").GetString()!;
    }
}
