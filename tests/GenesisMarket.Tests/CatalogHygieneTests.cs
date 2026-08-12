using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Domain.Enums;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Гигиена каталога: поднятие (лимит частоты), отметка «продано» и возврат,
/// восстановление из архива, автоархивация/предупреждения (джоб) и daysUntilArchive.
/// БД общая в рамках класса — тесты изолируются уникальными категориями/владельцами.
/// </summary>
public class CatalogHygieneTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    // ---- Поднятие (bump) ----

    [Fact]
    public async Task Bump_updates_timestamp_and_is_limited_to_once_per_cooldown()
    {
        var email = Unique("bump");
        var owner = await factory.SeedUserAsync(email, Password);
        var id = await factory.SeedListingAsync(owner, category: Category.Kids);
        var client = await AuthedClient(email);

        // Первое поднятие (BumpedAt ещё не задан) — успех.
        var first = await client.PostAsync($"/api/listings/{id}/bump", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var (_, bumpedAt, _, _, _) = await factory.LifecycleStateAsync(id);
        Assert.NotNull(bumpedAt);

        // Повторное сразу — упирается в лимит (429 с Retry-After).
        var second = await client.PostAsync($"/api/listings/{id}/bump", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.NotNull(second.Headers.RetryAfter);
    }

    [Fact]
    public async Task Bump_is_allowed_again_after_cooldown()
    {
        var email = Unique("bump-ok");
        var owner = await factory.SeedUserAsync(email, Password);
        var id = await factory.SeedListingAsync(owner, category: Category.Kids);
        // Последнее поднятие — 8 дней назад (лимит 7 дней уже прошёл).
        await factory.SetBumpTimestampsAsync(id, DateTimeOffset.UtcNow.AddDays(-8));
        var client = await AuthedClient(email);

        var resp = await client.PostAsync($"/api/listings/{id}/bump", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Bump_requires_active_status()
    {
        var email = Unique("bump-draft");
        var owner = await factory.SeedUserAsync(email, Password);
        var id = await factory.SeedListingAsync(owner, ListingStatus.Draft, category: Category.Kids);
        var client = await AuthedClient(email);

        var resp = await client.PostAsync($"/api/listings/{id}/bump", null);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Default_sort_puts_recently_bumped_first()
    {
        var ownerEmail = Unique("bump-sort");
        var owner = await factory.SeedUserAsync(ownerEmail, Password);
        const Category cat = Category.Animals;

        // Три активных объявления с разным «возрастом поднятия».
        var oldest = await factory.SeedListingAsync(owner, category: cat, title: "Старое объявление в топ");
        var middle = await factory.SeedListingAsync(owner, category: cat, title: "Среднее объявление списка");
        var newest = await factory.SeedListingAsync(owner, category: cat, title: "Свежее объявление списка");
        await factory.SetBumpTimestampsAsync(oldest, DateTimeOffset.UtcNow.AddDays(-10));
        await factory.SetBumpTimestampsAsync(middle, DateTimeOffset.UtcNow.AddDays(-5));
        await factory.SetBumpTimestampsAsync(newest, DateTimeOffset.UtcNow.AddDays(-1));

        var client = factory.CreateClient();
        // Без параметра sort — сортировка по умолчанию (по поднятию).
        var page = await client.GetFromJsonAsync<JsonElement>("/api/listings?category=animals&limit=50");
        var order = page.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToList();

        Assert.Equal(new[] { newest, middle, oldest }, order);

        // Поднимаем самое старое — оно должно всплыть на первое место.
        var ownerClient = await AuthedClient(ownerEmail);
        var bump = await ownerClient.PostAsync($"/api/listings/{oldest}/bump", null);
        Assert.Equal(HttpStatusCode.OK, bump.StatusCode);

        var after = await client.GetFromJsonAsync<JsonElement>("/api/listings?category=animals&limit=50");
        Assert.Equal(oldest, after.GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    // ---- Отметка «продано» ----

    [Fact]
    public async Task Mark_sold_leaves_direct_link_but_hides_from_catalog()
    {
        var email = Unique("sold");
        var owner = await factory.SeedUserAsync(email, Password);
        var id = await factory.SeedListingAsync(owner, category: Category.Services);
        var client = await AuthedClient(email);

        var sold = await client.PostAsync($"/api/listings/{id}/mark-sold", null);
        Assert.Equal(HttpStatusCode.OK, sold.StatusCode);
        Assert.Equal("Sold", (await sold.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        // Прямая ссылка живёт.
        var direct = await client.GetFromJsonAsync<JsonElement>($"/api/listings/{id}");
        Assert.Equal("Sold", direct.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, direct.GetProperty("daysUntilArchive").ValueKind);

        // Из каталога исчезло.
        var page = await client.GetFromJsonAsync<JsonElement>("/api/listings?category=services&limit=50");
        var ids = page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid());
        Assert.DoesNotContain(id, ids);
    }

    [Fact]
    public async Task Sold_can_be_reactivated_within_window_but_not_after()
    {
        var email = Unique("react");
        var owner = await factory.SeedUserAsync(email, Password);
        var id = await factory.SeedListingAsync(owner, category: Category.Services);
        var client = await AuthedClient(email);

        await client.PostAsync($"/api/listings/{id}/mark-sold", null);

        // В пределах окна (только что продано) — возврат разрешён.
        var back = await client.PostAsync($"/api/listings/{id}/reactivate", null);
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal("Active", (await back.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        // Продаём снова и сдвигаем SoldAt за пределы окна (7 дней) — возврат запрещён.
        await client.PostAsync($"/api/listings/{id}/mark-sold", null);
        await factory.SetSoldAtAsync(id, DateTimeOffset.UtcNow.AddDays(-8));
        var late = await client.PostAsync($"/api/listings/{id}/reactivate", null);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }

    // ---- Восстановление из архива ----

    [Fact]
    public async Task Restore_within_window_returns_to_active()
    {
        var email = Unique("restore");
        var owner = await factory.SeedUserAsync(email, Password);
        var id = await factory.SeedListingAsync(owner, category: Category.Home);
        var client = await AuthedClient(email);

        // Снятие с публикации → архив (проставляет ArchivedAt).
        var del = await client.DeleteAsync($"/api/listings/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var restore = await client.PostAsync($"/api/listings/{id}/restore", null);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        Assert.Equal("Active", (await restore.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Restore_after_ninety_days_is_rejected()
    {
        var email = Unique("restore-old");
        var owner = await factory.SeedUserAsync(email, Password);
        var id = await factory.SeedListingAsync(owner, category: Category.Home);
        var client = await AuthedClient(email);

        await client.DeleteAsync($"/api/listings/{id}");
        await factory.SetArchivedAtAsync(id, DateTimeOffset.UtcNow.AddDays(-91));

        var restore = await client.PostAsync($"/api/listings/{id}/restore", null);
        Assert.Equal(HttpStatusCode.Conflict, restore.StatusCode);
    }

    [Fact]
    public async Task Restore_goes_to_premoderation_when_user_had_recent_reject()
    {
        var email = Unique("restore-rej");
        var owner = await factory.SeedUserAsync(email, Password);
        var moderator = await factory.SeedUserAsync(Unique("mod"), Password, role: UserRole.Moderator);
        var id = await factory.SeedListingAsync(owner, category: Category.Home);
        var client = await AuthedClient(email);

        await client.DeleteAsync($"/api/listings/{id}");

        // У автора есть свежий reject по другому его объявлению → восстановление на премодерацию.
        var rejected = await factory.SeedListingAsync(owner, ListingStatus.Rejected, category: Category.Home);
        await factory.SeedRejectLogAsync(rejected, moderator);

        var restore = await client.PostAsync($"/api/listings/{id}/restore", null);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        Assert.Equal("PendingReview",
            (await restore.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    // ---- Автоархивация (джоб) ----

    [Fact]
    public async Task Hygiene_job_archives_expired_warns_upcoming_and_is_idempotent()
    {
        var expiredOwner = await factory.SeedUserAsync(Unique("job-exp"), Password);
        var warnOwner = await factory.SeedUserAsync(Unique("job-warn"), Password);
        var freshOwner = await factory.SeedUserAsync(Unique("job-fresh"), Password);

        var expired = await factory.SeedListingAsync(expiredOwner, category: Category.Transport);
        var upcoming = await factory.SeedListingAsync(warnOwner, category: Category.Transport);
        var fresh = await factory.SeedListingAsync(freshOwner, category: Category.Transport);

        await factory.SetBumpTimestampsAsync(expired, DateTimeOffset.UtcNow.AddDays(-31)); // за порогом 30 дней
        await factory.SetBumpTimestampsAsync(upcoming, DateTimeOffset.UtcNow.AddDays(-28)); // ≤3 дней до архивации
        // fresh оставляем свежим (BumpedAt=null, PublishedAt≈сейчас).

        await factory.RunCatalogHygieneAsync();

        var expiredState = await factory.LifecycleStateAsync(expired);
        Assert.Equal("Archived", expiredState.Status);
        Assert.NotNull(expiredState.ArchivedAt);

        var upcomingState = await factory.LifecycleStateAsync(upcoming);
        Assert.Equal("Active", upcomingState.Status);
        Assert.NotNull(upcomingState.ArchiveWarningAt);
        Assert.Equal(1, await factory.OutboxNotificationCountAsync(warnOwner));

        var freshState = await factory.LifecycleStateAsync(fresh);
        Assert.Equal("Active", freshState.Status);
        Assert.Null(freshState.ArchiveWarningAt);
        Assert.Equal(0, await factory.OutboxNotificationCountAsync(freshOwner));

        // Повторный прогон на тех же данных ничего не меняет (идемпотентность).
        await factory.RunCatalogHygieneAsync();
        Assert.Equal(1, await factory.OutboxNotificationCountAsync(warnOwner));
        Assert.Equal("Archived", (await factory.LifecycleStateAsync(expired)).Status);
    }

    [Fact]
    public async Task Bump_resets_archive_warning_and_days_until_archive()
    {
        var email = Unique("warn-reset");
        var owner = await factory.SeedUserAsync(email, Password);
        var id = await factory.SeedListingAsync(owner, category: Category.Electronics);
        await factory.SetBumpTimestampsAsync(id, DateTimeOffset.UtcNow.AddDays(-28));
        var client = await AuthedClient(email);

        // Джоб предупреждает автора.
        await factory.RunCatalogHygieneAsync();
        Assert.NotNull((await factory.LifecycleStateAsync(id)).ArchiveWarningAt);

        // Поднятие сбрасывает предупреждение и обновляет срок до архивации.
        var bump = await client.PostAsync($"/api/listings/{id}/bump", null);
        Assert.Equal(HttpStatusCode.OK, bump.StatusCode);
        var days = (await bump.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("daysUntilArchive").GetInt32();
        Assert.Equal(30, days);
        Assert.Null((await factory.LifecycleStateAsync(id)).ArchiveWarningAt);
    }

    [Fact]
    public async Task Days_until_archive_is_thirty_for_fresh_active_and_null_for_draft()
    {
        var email = Unique("days");
        var owner = await factory.SeedUserAsync(email, Password);
        var activeId = await factory.SeedListingAsync(owner, category: Category.Fashion);
        var draftId = await factory.SeedListingAsync(owner, ListingStatus.Draft, category: Category.Fashion);
        var client = await AuthedClient(email);

        var active = await client.GetFromJsonAsync<JsonElement>($"/api/listings/{activeId}");
        Assert.Equal(30, active.GetProperty("daysUntilArchive").GetInt32());

        var draft = await client.GetFromJsonAsync<JsonElement>($"/api/listings/{draftId}");
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("daysUntilArchive").ValueKind);
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
