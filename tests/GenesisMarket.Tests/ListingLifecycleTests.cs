using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GenesisMarket.Tests;

public class ListingLifecycleTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    [Fact]
    public async Task Other_user_cannot_edit_listing_returns_404()
    {
        var ownerId = await factory.SeedUserAsync(Unique("owner"), Password);
        var listingId = await factory.SeedListingAsync(ownerId);

        var otherEmail = Unique("other");
        await factory.SeedUserAsync(otherEmail, Password);
        var client = await AuthedClient(otherEmail);

        var resp = await client.PatchAsJsonAsync($"/api/listings/{listingId}", EditBody("Изменённый заголовок объявления"));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Patch_with_status_active_does_not_publish()
    {
        var email = Unique("draft");
        await factory.SeedUserAsync(email, Password, emailVerified: true);
        var client = await AuthedClient(email);

        // Черновик через API.
        var created = await client.PostAsJsonAsync("/api/listings", CreateBody("Продам велосипед горный", publish: false));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var listing = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = listing.GetProperty("id").GetGuid();
        Assert.Equal("Draft", listing.GetProperty("status").GetString());

        // PATCH пытается протащить status:"Active" в обход премодерации.
        var patch = await client.PatchAsJsonAsync($"/api/listings/{id}", new
        {
            title = "Продам велосипед горный обновлённый",
            description = "Отличный горный велосипед в хорошем состоянии, торг уместен",
            price = 1500,
            priceType = "fixed",
            category = "home",
            subcategoryId = 18,
            district = (string?)null,
            condition = "used",
            status = "Active"   // должно игнорироваться
        });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        // Статус не изменился — объявление осталось черновиком.
        var after = await client.GetFromJsonAsync<JsonElement>($"/api/listings/{id}");
        Assert.Equal("Draft", after.GetProperty("status").GetString());
        Assert.Equal("Продам велосипед горный обновлённый", after.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Thirty_first_active_listing_is_rejected()
    {
        var email = Unique("limit");
        var ownerId = await factory.SeedUserAsync(email, Password, emailVerified: true);

        // 30 активных объявлений уже есть.
        for (var i = 0; i < 30; i++)
            await factory.SeedListingAsync(ownerId);

        var client = await AuthedClient(email);
        var resp = await client.PostAsJsonAsync("/api/listings",
            CreateBody("Тридцать первое объявление тест", publish: true));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Create_draft_then_publish_goes_to_premoderation()
    {
        var email = Unique("publish");
        await factory.SeedUserAsync(email, Password, emailVerified: true);
        var client = await AuthedClient(email);

        var created = await client.PostAsJsonAsync("/api/listings", CreateBody("Продам гараж кирпичный", publish: false));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var publish = await client.PostAsync($"/api/listings/{id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

        // Новый автор: 0 одобренных объявлений, возраст аккаунта 0 дней → премодерация.
        var body = await publish.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PendingReview", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Duplicate_active_title_in_same_category_returns_409()
    {
        var email = Unique("dup");
        var ownerId = await factory.SeedUserAsync(email, Password, emailVerified: true);
        await factory.SeedListingAsync(ownerId, title: "Продам холодильник Атлант", category: Category.Home);

        var client = await AuthedClient(email);
        var resp = await client.PostAsJsonAsync("/api/listings",
            CreateBody("продам  ХОЛОДИЛЬНИК   атлант", publish: false)); // тот же нормализованный title

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Get_by_slug_returns_the_listing()
    {
        var email = Unique("slug");
        await factory.SeedUserAsync(email, Password, emailVerified: true);
        var client = await AuthedClient(email);

        var created = await client.PostAsJsonAsync("/api/listings", CreateBody("Продам ноутбук игровой", publish: false));
        var listing = await created.Content.ReadFromJsonAsync<JsonElement>();
        var slug = listing.GetProperty("slug").GetString();
        Assert.False(string.IsNullOrWhiteSpace(slug));

        var bySlug = await client.GetFromJsonAsync<JsonElement>($"/api/listings/by-slug/{slug}");
        Assert.Equal(listing.GetProperty("id").GetGuid(), bySlug.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task My_listings_filters_by_status()
    {
        var email = Unique("mine");
        var ownerId = await factory.SeedUserAsync(email, Password);
        await factory.SeedListingAsync(ownerId, ListingStatus.Active);
        await factory.SeedListingAsync(ownerId, ListingStatus.Draft);
        var client = await AuthedClient(email);

        var drafts = await client.GetFromJsonAsync<JsonElement>("/api/me/listings?status=Draft");
        Assert.Equal(1, drafts.GetArrayLength());
        Assert.Equal("Draft", drafts[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Create_with_short_title_is_rejected()
    {
        var email = Unique("short");
        await factory.SeedUserAsync(email, Password);
        var client = await AuthedClient(email);

        var resp = await client.PostAsJsonAsync("/api/listings", CreateBody("Коротко", publish: false)); // < 10
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    /// <summary>
    /// Счётчики для вкладок «Моих объявлений»: по одному числу на статус плюс
    /// "all". Проверяем каждое число, наличие нулевых статусов (клиент не должен
    /// дорисовывать их сам) и что "all" равен сумме остальных.
    /// </summary>
    [Fact]
    public async Task My_listing_counts_cover_every_status_and_sum_to_all()
    {
        var email = Unique("counts");
        var ownerId = await factory.SeedUserAsync(email, Password);

        // Draft 3, Active 2, Sold 1, Archived 1; PendingReview и Rejected — 0.
        for (var i = 0; i < 3; i++) await factory.SeedListingAsync(ownerId, ListingStatus.Draft);
        for (var i = 0; i < 2; i++) await factory.SeedListingAsync(ownerId, ListingStatus.Active);
        await factory.SeedListingAsync(ownerId, ListingStatus.Sold);
        await factory.SeedListingAsync(ownerId, ListingStatus.Archived);

        var client = await AuthedClient(email);
        var counts = await client.GetFromJsonAsync<JsonElement>("/api/me/listings/counts");

        Assert.Equal(3, counts.GetProperty("Draft").GetInt32());
        Assert.Equal(2, counts.GetProperty("Active").GetInt32());
        Assert.Equal(1, counts.GetProperty("Sold").GetInt32());
        Assert.Equal(1, counts.GetProperty("Archived").GetInt32());
        // Пустые статусы присутствуют явными нулями.
        Assert.Equal(0, counts.GetProperty("PendingReview").GetInt32());
        Assert.Equal(0, counts.GetProperty("Rejected").GetInt32());
        Assert.Equal(7, counts.GetProperty("all").GetInt32());

        // all — ровно сумма остальных ключей, без расхождений.
        var perStatus = Enum.GetValues<ListingStatus>()
            .Sum(s => counts.GetProperty(s.ToString()).GetInt32());
        Assert.Equal(counts.GetProperty("all").GetInt32(), perStatus);

        // Ключи статусов — те же литералы, что принимает фильтр списка:
        // берём их из ответа и проверяем, что по ним действительно фильтруется.
        var drafts = await client.GetFromJsonAsync<JsonElement>("/api/me/listings?status=Draft");
        Assert.Equal(counts.GetProperty("Draft").GetInt32(), drafts.GetArrayLength());

        // И "all" сходится с длиной невыфильтрованного списка.
        var all = await client.GetFromJsonAsync<JsonElement>("/api/me/listings");
        Assert.Equal(counts.GetProperty("all").GetInt32(), all.GetArrayLength());
    }

    /// <summary>
    /// Мягко удалённое объявление не попадает ни в список, ни в счётчики.
    /// У Listing стоит глобальный фильтр DeletedAt == null; если счётчики его
    /// обойдут (например, через IgnoreQueryFilters), сумма разойдётся с выдачей.
    /// </summary>
    [Fact]
    public async Task My_listing_counts_exclude_soft_deleted_listings()
    {
        var email = Unique("counts-deleted");
        var ownerId = await factory.SeedUserAsync(email, Password);
        await factory.SeedListingAsync(ownerId, ListingStatus.Active);
        var doomedId = await factory.SeedListingAsync(ownerId, ListingStatus.Active);

        var client = await AuthedClient(email);
        var before = await client.GetFromJsonAsync<JsonElement>("/api/me/listings/counts");
        Assert.Equal(2, before.GetProperty("Active").GetInt32());
        Assert.Equal(2, before.GetProperty("all").GetInt32());

        // Мягкое удаление — прямо в БД, минуя фильтр.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Listings.IgnoreQueryFilters()
                .Where(l => l.Id == doomedId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.DeletedAt, DateTimeOffset.UtcNow));
        }

        var after = await client.GetFromJsonAsync<JsonElement>("/api/me/listings/counts");
        Assert.Equal(1, after.GetProperty("Active").GetInt32());
        Assert.Equal(1, after.GetProperty("all").GetInt32());

        var list = await client.GetFromJsonAsync<JsonElement>("/api/me/listings");
        Assert.Equal(after.GetProperty("all").GetInt32(), list.GetArrayLength());
    }

    /// <summary>Счётчики — только свои: чужие объявления в них не видны.</summary>
    [Fact]
    public async Task My_listing_counts_never_include_other_users_listings()
    {
        var strangerId = await factory.SeedUserAsync(Unique("counts-stranger"), Password);
        for (var i = 0; i < 4; i++) await factory.SeedListingAsync(strangerId, ListingStatus.Active);

        var email = Unique("counts-own");
        var ownerId = await factory.SeedUserAsync(email, Password);
        await factory.SeedListingAsync(ownerId, ListingStatus.Draft);

        var client = await AuthedClient(email);
        var counts = await client.GetFromJsonAsync<JsonElement>("/api/me/listings/counts");

        Assert.Equal(1, counts.GetProperty("all").GetInt32());
        Assert.Equal(1, counts.GetProperty("Draft").GetInt32());
        Assert.Equal(0, counts.GetProperty("Active").GetInt32());
    }

    /// <summary>Счётчики закрыты авторизацией — это приватные данные владельца.</summary>
    [Fact]
    public async Task My_listing_counts_require_authentication()
    {
        var resp = await factory.CreateClient().GetAsync("/api/me/listings/counts");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ---- helpers ----

    private static object CreateBody(string title, bool publish) => new
    {
        title,
        description = "Отличное состояние, торг уместен, звоните в любое время дня",
        price = 1000,
        priceType = "fixed",
        category = "home",
        subcategoryId = 18,
        district = (string?)null,
        condition = "used",
        publish
    };

    private static object EditBody(string title) => new
    {
        title,
        description = "Обновлённое описание объявления, всё отлично работает",
        price = 2000,
        priceType = "fixed",
        category = "home",
        subcategoryId = 18,
        district = (string?)null,
        condition = "used"
    };

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
