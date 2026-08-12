using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Инструменты модератора: доступ строго по роли (обычный пользователь — 403),
/// очередь, одобрение/отклонение объявлений, разбор жалоб, бан/разбан в одной
/// транзакции (со SecurityStamp, архивацией объявлений и отзывом токенов) и
/// обязательный аудит чувствительной ручки с контактами пользователя.
/// </summary>
public class ModerationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    // ---- Доступ ----

    [Theory]
    [InlineData("GET", "/api/moderation/queue")]
    [InlineData("GET", "/api/moderation/stats")]
    [InlineData("GET", "/api/moderation/listings/{id}")]
    [InlineData("POST", "/api/moderation/listings/{id}/approve")]
    [InlineData("POST", "/api/moderation/users/{id}/ban")]
    public async Task Regular_user_gets_403_on_moderation_endpoints(string method, string path)
    {
        var userEmail = Unique("plainuser");
        await factory.SeedUserAsync(userEmail, Password);
        var client = await AuthedClient(userEmail);

        var url = path.Replace("{id}", Guid.NewGuid().ToString());
        var resp = method == "GET"
            ? await client.GetAsync(url)
            : await client.PostAsync(url, JsonContent.Create(new { reason = "Spam" }));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Anonymous_gets_401_on_moderation()
    {
        var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/moderation/queue");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ---- Очередь ----

    [Fact]
    public async Task Queue_puts_autoflagged_listings_before_reports_and_older()
    {
        var mod = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);

        // Автофлаг — объявление с приоритетом; плюс обычная жалоба (priority 0).
        var flagged = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview);
        await factory.SetListingPriorityAsync(flagged, 100);
        var listing2 = await factory.SeedListingAsync(sellerId);
        var report = await factory.SeedReportAsync(listing2, reason: ReportReason.Fraud);

        using var doc = await GetJson(mod, "/api/moderation/queue?limit=50");
        var items = doc.RootElement.GetProperty("items");

        // Первый элемент — автофлаг (priority > 0), Kind = Listing.
        var first = items[0];
        Assert.Equal("Listing", first.GetProperty("kind").GetString());
        Assert.True(first.GetProperty("priority").GetInt32() > 0);

        // Наша жалоба присутствует в очереди.
        Assert.Contains(EnumerateItems(items),
            i => i.GetProperty("kind").GetString() == "Report" &&
                 i.GetProperty("id").GetGuid() == report);
        Assert.Contains(EnumerateItems(items),
            i => i.GetProperty("id").GetGuid() == flagged);
    }

    [Fact]
    public async Task Queue_reason_filter_returns_only_matching_reports()
    {
        var mod = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var listing = await factory.SeedListingAsync(sellerId);

        var spam = await factory.SeedReportAsync(listing, reason: ReportReason.Spam);
        var fraud = await factory.SeedReportAsync(listing, reason: ReportReason.Fraud);

        using var doc = await GetJson(mod, "/api/moderation/queue?reason=Fraud&limit=50");
        var ids = EnumerateItems(doc.RootElement.GetProperty("items"))
            .Select(i => i.GetProperty("id").GetGuid())
            .ToList();

        Assert.Contains(fraud, ids);
        Assert.DoesNotContain(spam, ids);
        // Причина сужает выдачу до жалоб — объявлений на премодерации в ней нет.
        Assert.All(EnumerateItems(doc.RootElement.GetProperty("items")),
            i => Assert.Equal("Report", i.GetProperty("kind").GetString()));
    }

    // ---- Одобрение / отклонение ----

    [Fact]
    public async Task Approve_activates_pending_listing_and_logs()
    {
        var mod = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var listingId = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview);

        var resp = await mod.PostAsync($"/api/moderation/listings/{listingId}/approve", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Equal("Active", (await factory.ListingModerationAsync(listingId)).Status);
        Assert.True(await factory.ModerationLogCountAsync(listingId, "listing.approve") >= 1);
    }

    [Fact]
    public async Task Approve_non_pending_listing_returns_409()
    {
        var mod = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var listingId = await factory.SeedListingAsync(sellerId, ListingStatus.Active);

        var resp = await mod.PostAsync($"/api/moderation/listings/{listingId}/approve", null);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Reject_marks_rejected_writes_outbox_notification_and_logs()
    {
        var mod = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var listingId = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview);

        var resp = await mod.PostAsJsonAsync($"/api/moderation/listings/{listingId}/reject",
            new { reason = "Prohibited", comment = "Запрещённый товар" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Equal("Rejected", (await factory.ListingModerationAsync(listingId)).Status);
        // Уведомление автора ушло в outbox.
        Assert.True(await factory.OutboxCountAsync(OutboxMessage.ListingRejected, listingId) >= 1);
        // Причина и факт отклонения записаны в журнал.
        Assert.True(await factory.ModerationLogCountAsync(listingId, "listing.reject") >= 1);
    }

    // ---- Жалобы ----

    [Fact]
    public async Task Resolve_report_closes_it_and_logs()
    {
        var mod = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var listingId = await factory.SeedListingAsync(sellerId);
        var reportId = await factory.SeedReportAsync(listingId, reason: ReportReason.Spam);

        var resp = await mod.PostAsJsonAsync($"/api/moderation/reports/{reportId}/resolve",
            new { status = "Resolved", resolution = "Нарушение подтверждено" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.True(await factory.ModerationLogCountAsync(reportId, "report.resolve") >= 1);

        // Закрытой жалобы больше нет в очереди.
        using var doc = await GetJson(mod, "/api/moderation/queue?limit=50");
        var ids = EnumerateItems(doc.RootElement.GetProperty("items"))
            .Select(i => i.GetProperty("id").GetGuid());
        Assert.DoesNotContain(reportId, ids);
    }

    [Fact]
    public async Task Resolve_with_invalid_status_returns_400()
    {
        var mod = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var listingId = await factory.SeedListingAsync(sellerId);
        var reportId = await factory.SeedReportAsync(listingId);

        var resp = await mod.PostAsJsonAsync($"/api/moderation/reports/{reportId}/resolve",
            new { status = "New", resolution = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ---- Бан / разбан ----

    [Fact]
    public async Task Ban_bans_user_archives_listings_and_kills_tokens()
    {
        var mod = await Moderator();

        // Живой пользователь с реальными токенами.
        var (victimEmail, victimId) = await RegisterAndGetId("victim");
        var (victimClient, refreshToken) = await LoginWithRefresh(victimEmail);

        // Активное объявление с уникальным словом в заголовке — проверим каталог.
        var marker = "зонтикбантест";
        var listingId = await factory.SeedListingAsync(victimId,
            ListingStatus.Active, title: $"Продаю {marker} новый");

        // До бана объявление находится через каталог.
        Assert.Contains(listingId, await CatalogSearchIds(marker));

        var ban = await mod.PostAsJsonAsync($"/api/moderation/users/{victimId}/ban",
            new { reason = "Мошенничество", until = (DateTimeOffset?)null });
        Assert.Equal(HttpStatusCode.OK, ban.StatusCode);

        // Пользователь забанен, объявление архивировано.
        Assert.True((await factory.UserBanStateAsync(victimId)).IsBanned);
        Assert.Equal("Archived", (await factory.ListingModerationAsync(listingId)).Status);
        Assert.True(await factory.ModerationLogCountAsync(victimId, "user.ban") >= 1);

        // После бана объявление не видно в каталоге.
        Assert.DoesNotContain(listingId, await CatalogSearchIds(marker));

        // Забаненный не может создать объявление (старый access-токен отвергнут).
        var create = await victimClient.PostAsJsonAsync("/api/listings", new
        {
            title = "Новое объявление забаненного",
            description = "Описание достаточной длины для валидатора объявления",
            price = 100,
            priceType = "Fixed",
            category = "home",
            subcategoryId = 18,
            city = "tiraspol",
            district = (string?)null,
            condition = "Used",
            publish = false
        });
        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);

        // Забаненный не может обновить токен (все refresh-токены отозваны).
        var refresh = await factory.CreateClient()
            .PostAsJsonAsync("/api/auth/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Cannot_ban_self()
    {
        var modEmail = Unique("selfmod");
        var modId = await factory.SeedUserAsync(modEmail, Password, role: UserRole.Moderator);
        var mod = await AuthedClient(modEmail);

        var resp = await mod.PostAsJsonAsync($"/api/moderation/users/{modId}/ban",
            new { reason = "test", until = (DateTimeOffset?)null });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Unban_lifts_ban_and_logs()
    {
        var mod = await Moderator();
        var victimId = await factory.SeedUserAsync(Unique("victim"), Password, banned: true);

        var resp = await mod.PostAsync($"/api/moderation/users/{victimId}/unban", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.False((await factory.UserBanStateAsync(victimId)).IsBanned);
        Assert.True(await factory.ModerationLogCountAsync(victimId, "user.unban") >= 1);
    }

    // ---- Чувствительная ручка: контакты пользователя ----

    [Fact]
    public async Task User_contacts_returns_pii_and_logs_every_call()
    {
        var mod = await Moderator();
        var email = Unique("contactowner");
        var victimId = await factory.SeedUserAsync(email, Password);

        var r1 = await mod.GetAsync($"/api/moderation/users/{victimId}");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        using (var doc = JsonDocument.Parse(await r1.Content.ReadAsStringAsync()))
        {
            Assert.Equal(email, doc.RootElement.GetProperty("email").GetString());
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("phoneE164").GetString()));
        }

        // Второй вызов — вторая запись в журнал: логируется КАЖДЫЙ просмотр.
        var r2 = await mod.GetAsync($"/api/moderation/users/{victimId}");
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

        Assert.True(await factory.ModerationLogCountAsync(victimId, "user.view_contacts") >= 2);
    }

    [Fact]
    public async Task User_contacts_forbidden_for_regular_user()
    {
        var victimId = await factory.SeedUserAsync(Unique("victim"), Password);
        var plain = await AuthedClient(await SeedPlainUser());

        var resp = await plain.GetAsync($"/api/moderation/users/{victimId}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        // Ни одна запись журнала не создана — доступ отсечён до экшена.
        Assert.Equal(0, await factory.ModerationLogCountAsync(victimId, "user.view_contacts"));
    }

    // ---- Статистика ----

    [Fact]
    public async Task Stats_counts_queue_and_actions()
    {
        var mod = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview);

        using var doc = await GetJson(mod, "/api/moderation/stats");
        var root = doc.RootElement;

        Assert.True(root.GetProperty("pendingListings").GetInt32() >= 1);
        Assert.True(root.GetProperty("queueTotal").GetInt32() >= 1);
        // Поля присутствуют и неотрицательны.
        Assert.True(root.GetProperty("actionsToday").GetInt32() >= 0);
        Assert.True(root.GetProperty("actionsThisWeek").GetInt32() >= 0);
    }

    // ---- helpers ----

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";

    private async Task<string> SeedPlainUser()
    {
        var email = Unique("plain");
        await factory.SeedUserAsync(email, Password);
        return email;
    }

    private async Task<HttpClient> Moderator()
    {
        var email = Unique("mod");
        await factory.SeedUserAsync(email, Password, role: UserRole.Moderator);
        return await AuthedClient(email);
    }

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

    /// <summary>Регистрирует живого пользователя (через API) и возвращает его id.</summary>
    private async Task<(string Email, Guid Id)> RegisterAndGetId(string prefix)
    {
        var email = Unique(prefix);
        var client = factory.CreateClient();
        var reg = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = Password,
            displayName = "Тестовый",
            city = "tiraspol",
            phone = "+37377100200"
        });
        Assert.Equal(HttpStatusCode.OK, reg.StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("user").GetProperty("id").GetGuid();
        return (email, id);
    }

    private async Task<(HttpClient Client, string RefreshToken)> LoginWithRefresh(string email)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var access = doc.RootElement.GetProperty("accessToken").GetString();
        var refresh = doc.RootElement.GetProperty("refreshToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", access);
        return (client, refresh);
    }

    private async Task<List<Guid>> CatalogSearchIds(string q)
    {
        var client = factory.CreateClient();
        using var doc = await GetJson(client, $"/api/listings?q={Uri.EscapeDataString(q)}&limit=50");
        return EnumerateItems(doc.RootElement.GetProperty("items"))
            .Select(i => i.GetProperty("id").GetGuid())
            .ToList();
    }

    private static async Task<JsonDocument> GetJson(HttpClient client, string url)
    {
        var resp = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    }

    private static IEnumerable<JsonElement> EnumerateItems(JsonElement items)
    {
        foreach (var item in items.EnumerateArray())
            yield return item;
    }
}
