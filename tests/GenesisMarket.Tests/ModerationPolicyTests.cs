using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Domain.Enums;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Политика «нужен ли модератор»: скоринг автора и контента, три режима публикации
/// (без проверки / постмодерация / премодерация), возврат на проверку после правки
/// и денормализованные сигналы доверия (триггер listings_trust_sync).
/// </summary>
public class ModerationPolicyTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    // ---- Шаг 1: доверие считается по одобренным, а не по поданным ----

    [Fact]
    public async Task New_author_goes_to_premoderation()
    {
        var email = Unique("newbie");
        await factory.SeedUserAsync(email, Password, emailVerified: true, phoneVerified: false);
        var client = await AuthedClient(email);

        var body = await Publish(client, "Продам гараж кирпичный сухой");

        // 0 одобренных + возраст 0 дней + только почта = 10 очков → премодерация.
        Assert.Equal("PendingReview", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Submitted_but_rejected_listings_do_not_earn_trust()
    {
        var email = Unique("rejected-history");
        var userId = await factory.SeedUserAsync(email, Password, emailVerified: true);

        // Старый аккаунт с тремя ПОДАННЫМИ и отклонёнными объявлениями. Прежний счётчик
        // считал факт подачи (PublishedAt) — и такой автор уходил в автопубликацию.
        await factory.SetAuthorTrustAsync(userId, createdAt: DateTimeOffset.UtcNow.AddDays(-60));
        for (var i = 0; i < 3; i++)
            await factory.SeedListingAsync(userId, ListingStatus.Rejected, title: $"Отклонённое объявление {i}");

        var trust = await factory.AuthorTrustAsync(userId);
        Assert.Equal(0, trust.ApprovedListings);

        var client = await AuthedClient(email);
        var body = await Publish(client, "Ещё одно объявление после отказов");
        var id = body.GetProperty("id").GetGuid();

        // Возраст, почта и телефон дают проходной балл, но одобренных объявлений нет —
        // автопубликации не будет. Объявление идёт в каталог под присмотром, а не мимо него.
        var state = await factory.ListingModerationStateAsync(id);
        Assert.NotNull(state.ReviewQueuedAt);
        Assert.Null(state.ApprovedAt);
    }

    [Fact]
    public async Task Recent_reject_closes_auto_publish_for_trusted_author()
    {
        var email = Unique("recent-reject");
        var userId = await factory.SeedUserAsync(email, Password, emailVerified: true);

        // Заведомо доверенный автор: 5 одобренных, старый аккаунт, телефон подтверждён.
        await factory.SetAuthorTrustAsync(userId,
            approvedListings: 5,
            createdAt: DateTimeOffset.UtcNow.AddDays(-90),
            lastRejectedAt: DateTimeOffset.UtcNow.AddDays(-1));

        var client = await AuthedClient(email);
        var body = await Publish(client, "Продам шкаф трёхдверный светлый");

        // Свежий отказ — жёсткое правило, сильнее накопленного доверия.
        Assert.Equal("PendingReview", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Trusted_author_publishes_without_moderator()
    {
        var email = Unique("trusted");
        var userId = await factory.SeedUserAsync(email, Password, emailVerified: true);
        await factory.SetAuthorTrustAsync(userId,
            approvedListings: 3, createdAt: DateTimeOffset.UtcNow.AddDays(-40));

        var client = await AuthedClient(email);
        var body = await Publish(client, "Продам стол письменный дубовый");
        var id = body.GetProperty("id").GetGuid();

        Assert.Equal("Active", body.GetProperty("status").GetString());

        // Без проверки — значит и в очереди модерации его нет, и доверие сразу засчитано.
        var state = await factory.ListingModerationStateAsync(id);
        Assert.Null(state.ReviewQueuedAt);
        Assert.NotNull(state.ApprovedAt);

        var trust = await factory.AuthorTrustAsync(userId);
        Assert.Equal(4, trust.ApprovedListings); // триггер listings_trust_sync
    }

    [Fact]
    public async Task Aged_account_without_approved_listings_never_reaches_auto_publish()
    {
        var email = Unique("aged-empty");
        var userId = await factory.SeedUserAsync(email, Password, emailVerified: true);

        // Отстоянный аккаунт с подтверждённым телефоном набирает проходной балл,
        // ни разу ничего не опубликовав — пол по одобренным должен это перекрыть.
        await factory.SetAuthorTrustAsync(userId,
            approvedListings: 0, createdAt: DateTimeOffset.UtcNow.AddDays(-120));

        var client = await AuthedClient(email);
        var body = await Publish(client, "Продам кресло компьютерное чёрное");
        var id = body.GetProperty("id").GetGuid();

        Assert.Equal("Active", body.GetProperty("status").GetString());

        var state = await factory.ListingModerationStateAsync(id);
        Assert.NotNull(state.ReviewQueuedAt);   // постмодерация, а не публикация без проверки
        Assert.Null(state.ApprovedAt);          // доверие пока не засчитано
    }

    // ---- Шаг 2: постмодерация ----

    [Fact]
    public async Task Post_moderation_listing_is_in_catalog_and_in_queue()
    {
        var email = Unique("postmod");
        var userId = await factory.SeedUserAsync(email, Password, emailVerified: true);
        await factory.SetAuthorTrustAsync(userId,
            approvedListings: 0, createdAt: DateTimeOffset.UtcNow.AddDays(-120));

        var client = await AuthedClient(email);
        var id = (await Publish(client, "Продам велотренажёр магнитный")).GetProperty("id").GetGuid();

        // Видно всем: объявление уже в каталоге, модератор его не задерживал.
        var card = await client.GetFromJsonAsync<JsonElement>($"/api/listings/{id}");
        Assert.Equal("Active", card.GetProperty("status").GetString());

        // И одновременно — в очереди модератора, со статусом Active (постмодерация).
        var mod = await ModeratorClient();
        using var queue = await GetJson(mod, "/api/moderation/queue?type=Listing&limit=50");
        var item = queue.RootElement.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetGuid() == id);
        Assert.Equal("Active", item.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Approving_post_moderation_listing_clears_queue()
    {
        var ownerId = await factory.SeedUserAsync(Unique("postmod-approve"), Password);
        var listingId = await factory.SeedListingAsync(ownerId, ListingStatus.Active);
        await factory.QueueForPostReviewAsync(listingId);

        var before = await factory.AuthorTrustAsync(ownerId);

        var mod = await ModeratorClient();
        var resp = await mod.PostAsync($"/api/moderation/listings/{listingId}/approve", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var state = await factory.ListingModerationStateAsync(listingId);
        Assert.Equal(ListingStatus.Active, state.Status);
        Assert.Null(state.ReviewQueuedAt);

        // ApprovedAt объявление получило ещё при сидировании (оно дошло до каталога),
        // поэтому доверие уже засчитано и повторно не растёт.
        Assert.NotNull(state.ApprovedAt);
        Assert.Equal(before.ApprovedListings, (await factory.AuthorTrustAsync(ownerId)).ApprovedListings);
    }

    [Fact]
    public async Task Rejecting_post_moderation_listing_removes_it_from_catalog()
    {
        var ownerId = await factory.SeedUserAsync(Unique("postmod-reject"), Password);
        var listingId = await factory.SeedListingAsync(ownerId, ListingStatus.Active);
        await factory.QueueForPostReviewAsync(listingId);

        var mod = await ModeratorClient();
        var resp = await mod.PostAsJsonAsync($"/api/moderation/listings/{listingId}/reject",
            new { reason = "Prohibited", comment = "Запрещённый товар" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var state = await factory.ListingModerationStateAsync(listingId);
        Assert.Equal(ListingStatus.Rejected, state.Status);
        Assert.Null(state.ReviewQueuedAt);

        // Триггер зафиксировал отказ — автопубликация автору теперь закрыта.
        var trust = await factory.AuthorTrustAsync(ownerId);
        Assert.NotNull(trust.LastRejectedAt);
    }

    [Fact]
    public async Task Substantial_edit_of_active_listing_returns_it_to_review()
    {
        var email = Unique("edit-bypass");
        var userId = await factory.SeedUserAsync(email, Password, emailVerified: true);
        await factory.SetAuthorTrustAsync(userId,
            approvedListings: 3, createdAt: DateTimeOffset.UtcNow.AddDays(-40));

        var client = await AuthedClient(email);
        var id = (await Publish(client, "Продам микроволновку белую")).GetProperty("id").GetGuid();
        Assert.Null((await factory.ListingModerationStateAsync(id)).ReviewQueuedAt);

        // Классический обход: опубликовать безобидное, потом переписать текст.
        var patch = await client.PatchAsJsonAsync($"/api/listings/{id}", new
        {
            title = "Продам микроволновку белую",
            description = "Пишите в телеграм t.me/prodavec1 или на сайт example-shop.com, цена договорная",
            price = 1000,
            priceType = "fixed",
            category = "home",
            subcategoryId = 18,
            district = (string?)null,
            condition = "used"
        });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var state = await factory.ListingModerationStateAsync(id);
        Assert.NotNull(state.ReviewQueuedAt);
    }

    [Fact]
    public async Task Cosmetic_edit_does_not_return_listing_to_review()
    {
        var email = Unique("edit-cosmetic");
        var userId = await factory.SeedUserAsync(email, Password, emailVerified: true);
        await factory.SetAuthorTrustAsync(userId,
            approvedListings: 3, createdAt: DateTimeOffset.UtcNow.AddDays(-40));

        var client = await AuthedClient(email);
        var created = await Publish(client, "Продам холодильник двухкамерный");
        var id = created.GetProperty("id").GetGuid();

        // Меняем только район — на оценку модерации это не влияет.
        var patch = await client.PatchAsJsonAsync($"/api/listings/{id}", new
        {
            title = created.GetProperty("title").GetString(),
            description = created.GetProperty("description").GetString(),
            price = created.GetProperty("price").GetDecimal(),
            priceType = "fixed",
            category = "home",
            subcategoryId = 18,
            district = "Октябрьский",
            condition = "used"
        });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        Assert.Null((await factory.ListingModerationStateAsync(id)).ReviewQueuedAt);
    }

    [Fact]
    public async Task Restore_from_archive_requires_verified_contact()
    {
        // Восстановление возвращает объявление в каталог, то есть публикует его.
        // Автор без подтверждённой почты опубликовать не может — и через restore тоже.
        var email = Unique("restore-guard");
        var ownerId = await factory.SeedUserAsync(email, Password, emailVerified: false);
        var listingId = await factory.SeedListingAsync(ownerId, ListingStatus.Archived);

        var client = await AuthedClient(email);
        var resp = await client.PostAsync($"/api/listings/{listingId}/restore", null);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal(ListingStatus.Archived,
            (await factory.ListingModerationStateAsync(listingId)).Status);
    }

    // ---- Шаг 3: риск контента ----

    [Fact]
    public async Task Stop_word_forces_premoderation_even_for_trusted_author()
    {
        var email = Unique("stopword");
        var userId = await factory.SeedUserAsync(email, Password, emailVerified: true);
        await factory.SetAuthorTrustAsync(userId,
            approvedListings: 5, createdAt: DateTimeOffset.UtcNow.AddDays(-200));

        var client = await AuthedClient(email);
        var body = await Publish(client, "Продам сейф оружейный металлический",
            description: "Продам оружие в отличном состоянии, торг уместен при осмотре");

        Assert.Equal("PendingReview", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task External_link_downgrades_trusted_author_to_post_moderation()
    {
        var email = Unique("link-risk");
        var userId = await factory.SeedUserAsync(email, Password, emailVerified: true);
        await factory.SetAuthorTrustAsync(userId,
            approvedListings: 3, createdAt: DateTimeOffset.UtcNow.AddDays(-40));

        var client = await AuthedClient(email);
        var clean = await Publish(client, "Продам самокат электрический синий");
        Assert.Null((await factory.ListingModerationStateAsync(clean.GetProperty("id").GetGuid())).ReviewQueuedAt);

        // Отличие от чистого объявления ровно одно — ссылка наружу.
        var risky = await Publish(client, "Продам самокат электрический красный",
            description: "Подробности и фото на сайте moy-magazin.com, самовывоз, торг уместен");
        var id = risky.GetProperty("id").GetGuid();

        // В каталог пускаем (автор доверен), но показываем модератору.
        Assert.Equal("Active", risky.GetProperty("status").GetString());
        Assert.NotNull((await factory.ListingModerationStateAsync(id)).ReviewQueuedAt);
    }

    [Fact]
    public async Task Risky_listing_gets_higher_queue_priority()
    {
        var email = Unique("priority");
        var userId = await factory.SeedUserAsync(email, Password, emailVerified: true);
        await factory.SetAuthorTrustAsync(userId,
            approvedListings: 0, createdAt: DateTimeOffset.UtcNow.AddDays(-120));

        var client = await AuthedClient(email);
        var plain = (await Publish(client, "Продам тумбочку прикроватную"))
            .GetProperty("id").GetGuid();
        var risky = (await Publish(client, "Продам квартиру двухкомнатную центр",
                category: "realestate", subcategoryId: 1, price: 500000,
                description: "Срочно, подробности в телеграм @prodavec2024, торг при осмотре"))
            .GetProperty("id").GetGuid();

        var mod = await ModeratorClient();
        using var queue = await GetJson(mod, "/api/moderation/queue?type=Listing&limit=50");
        var items = queue.RootElement.GetProperty("items").EnumerateArray().ToList();

        var riskyPriority = items.Single(i => i.GetProperty("id").GetGuid() == risky)
            .GetProperty("priority").GetInt32();
        var plainPriority = items.Single(i => i.GetProperty("id").GetGuid() == plain)
            .GetProperty("priority").GetInt32();

        Assert.True(riskyPriority > plainPriority,
            $"рисковое объявление должно стоять выше в очереди: {riskyPriority} vs {plainPriority}");
    }

    // ---- helpers ----

    private async Task<JsonElement> Publish(
        HttpClient client, string title, string? description = null,
        string category = "home", int subcategoryId = 18, decimal price = 1000)
    {
        var resp = await client.PostAsJsonAsync("/api/listings", new
        {
            title,
            description = description ?? "Отличное состояние, самовывоз, торг уместен при осмотре",
            price,
            priceType = "fixed",
            category,
            subcategoryId,
            district = (string?)null,
            condition = "used",
            publish = true
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";

    private async Task<HttpClient> ModeratorClient()
    {
        var email = Unique("moderator");
        await factory.SeedUserAsync(email, Password, role: UserRole.Moderator);
        return await AuthedClient(email);
    }

    private static async Task<JsonDocument> GetJson(HttpClient client, string url)
    {
        var resp = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    }

    private async Task<HttpClient> AuthedClient(string email)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", doc.RootElement.GetProperty("accessToken").GetString());
        return client;
    }
}
