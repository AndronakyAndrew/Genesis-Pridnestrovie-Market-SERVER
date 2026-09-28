using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Справочники каталога в БД: сид миграции, публичная выдача, управление администратором
/// (создание, переименование, выключение, журнал), регистр кодов на входе и запрет
/// выключенных значений для новых объявлений.
/// </summary>
public class CatalogReferencesTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    [Fact]
    public async Task Migration_seeds_former_enum_values_with_labels_and_slugs()
    {
        using var doc = JsonDocument.Parse(await factory.CreateClient().GetStringAsync("/api/catalog/references"));
        var cities = doc.RootElement.GetProperty("cities").EnumerateArray().ToList();
        var categories = doc.RootElement.GetProperty("categories").EnumerateArray().ToList();

        Assert.Contains(cities, c => c.GetProperty("code").GetString() == City.Tiraspol &&
                                     c.GetProperty("name").GetString() == "Тирасполь" &&
                                     c.GetProperty("slug").GetString() == "tiraspol");
        Assert.Contains(categories, c => c.GetProperty("code").GetString() == Category.RealEstate &&
                                         c.GetProperty("slug").GetString() == "realestate");
        Assert.True(cities.Count >= 8);
        Assert.True(categories.Count >= 10);
        Assert.True(doc.RootElement.GetProperty("subcategories").GetArrayLength() >= 42);
    }

    [Fact]
    public async Task Lowercase_codes_are_accepted_and_stored_canonically()
    {
        var email = Unique("seller");
        await factory.SeedUserAsync(email, Password);
        var seller = await Login(email);

        var create = await seller.PostAsJsonAsync("/api/listings", NewListing(category: "home", city: "bendery"));
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        Assert.Equal("Home", created.RootElement.GetProperty("category").GetString());
        Assert.Equal("Bendery", created.RootElement.GetProperty("city").GetString());

        // Фильтр каталога тоже регистронезависим; неизвестный код — 400.
        Assert.Equal(HttpStatusCode.OK,
            (await factory.CreateClient().GetAsync("/api/listings?category=HOME&cities=bendery")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await factory.CreateClient().GetAsync("/api/listings?category=Spaceships")).StatusCode);
    }

    [Fact]
    public async Task Admin_adds_city_renames_it_and_it_appears_publicly_with_audit()
    {
        var (admin, adminId) = await Staff(UserRole.Admin);
        var code = "Testgrad" + Guid.NewGuid().ToString("N")[..6];
        var slug = code.ToLowerInvariant();

        var create = await admin.PostAsJsonAsync("/api/admin/catalog/cities",
            new { code, name = "Тестоград", slug, sortOrder = 999 });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        // Тот же код в другом регистре — дубль.
        var dup = await admin.PostAsJsonAsync("/api/admin/catalog/cities",
            new { code = code.ToUpperInvariant()[..1] + code[1..].ToUpperInvariant(), name = "Дубль", slug = slug + "-2" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        var rename = await admin.PatchAsJsonAsync($"/api/admin/catalog/cities/{code}", new { name = "Новоград" });
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);

        using var doc = JsonDocument.Parse(await factory.CreateClient().GetStringAsync("/api/catalog/references"));
        Assert.Contains(doc.RootElement.GetProperty("cities").EnumerateArray(),
            c => c.GetProperty("code").GetString() == code && c.GetProperty("name").GetString() == "Новоград");

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entries = await db.AuditEvents.Where(e => e.TargetType == "city" && e.TargetId == code)
            .OrderBy(e => e.OccurredAt).ToListAsync();
        Assert.Equal(["catalog.create", "catalog.update"], entries.Select(e => e.Action));
        Assert.All(entries, e => Assert.Equal(adminId, e.ActorId));
        Assert.Contains("Тестоград", entries[1].OldValue);
        Assert.Contains("Новоград", entries[1].NewValue);
    }

    [Fact]
    public async Task Deactivated_category_is_rejected_for_new_listings_but_existing_stay_valid()
    {
        var (admin, _) = await Staff(UserRole.Admin);
        var code = "Retro" + Guid.NewGuid().ToString("N")[..6];
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/admin/catalog/categories",
            new { code, name = "Ретро", slug = code.ToLowerInvariant() })).StatusCode);

        using var sub = JsonDocument.Parse(await (await admin.PostAsJsonAsync("/api/admin/catalog/subcategories",
            new { category = code, slug = "plastinki", name = "Пластинки" })).Content.ReadAsStringAsync());
        var subId = sub.RootElement.GetProperty("id").GetInt32();
        Assert.True(subId >= 1000, "Id новых подкатегорий — из identity начиная с 1000.");

        var email = Unique("retro");
        await factory.SeedUserAsync(email, Password);
        var seller = await Login(email);
        var ok = await seller.PostAsJsonAsync("/api/listings", NewListing(category: code, subcategoryId: subId));
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK,
            (await admin.PatchAsJsonAsync($"/api/admin/catalog/categories/{code}", new { isActive = false })).StatusCode);

        var rejected = await seller.PostAsJsonAsync("/api/listings",
            NewListing(category: code, subcategoryId: subId, title: "Другое ретро объявление"));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        // Выключенная категория видна в справочнике с флагом — для подписей старых объявлений.
        using var refs = JsonDocument.Parse(await factory.CreateClient().GetStringAsync("/api/catalog/references"));
        Assert.Contains(refs.RootElement.GetProperty("categories").EnumerateArray(),
            c => c.GetProperty("code").GetString() == code && !c.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Moderator_cannot_manage_catalog()
    {
        var (mod, _) = await Staff(UserRole.Moderator);
        var resp = await mod.PostAsJsonAsync("/api/admin/catalog/cities",
            new { code = "Nowhere", name = "Нигде", slug = "nowhere" });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ---- helpers ----

    private static object NewListing(
        string category = "Home", string city = "Tiraspol", int subcategoryId = 18,
        string title = "Диван угловой почти новый") => new
    {
        title,
        description = "Описание достаточной длины для валидатора объявления",
        price = 1500,
        priceType = "Fixed",
        category,
        subcategoryId,
        city,
        district = (string?)null,
        condition = "Used",
        publish = false
    };

    private async Task<(HttpClient Client, Guid Id)> Staff(UserRole role)
    {
        var email = Unique(role.ToString().ToLowerInvariant());
        var id = await factory.SeedUserAsync(email, Password, role: role);
        return (await Login(email), id);
    }

    private async Task<HttpClient> Login(string email)
    {
        var client = factory.CreateClient();
        var login = await factory.LoginAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new("Bearer", doc.RootElement.GetProperty("accessToken").GetString());
        return client;
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";
}
