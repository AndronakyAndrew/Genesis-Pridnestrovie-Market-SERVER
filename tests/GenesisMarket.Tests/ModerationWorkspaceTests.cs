using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Domain.Enums;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Рабочее место модератора, чтение: журнал действий, реестр пользователей с досье,
/// экран жалоб (список, карточка, «взять в работу»). Контакты в этих ответах не
/// появляются нигде — только в журналируемой ручке карточки контактов.
/// </summary>
public class ModerationWorkspaceTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    // ---- доступ ----

    [Theory]
    [InlineData("/api/moderation/logs")]
    [InlineData("/api/moderation/logs/summary")]
    [InlineData("/api/moderation/logs/actors")]
    [InlineData("/api/moderation/users")]
    [InlineData("/api/moderation/users/summary")]
    [InlineData("/api/moderation/reports")]
    [InlineData("/api/moderation/reports/summary")]
    public async Task Regular_user_gets_403(string url)
    {
        var email = Unique("plain");
        await factory.SeedUserAsync(email, Password);
        var client = await AuthedClient(email);

        var resp = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ---- журнал ----

    [Fact]
    public async Task Log_shows_decision_with_actor_target_and_wait_time()
    {
        var (mod, _) = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var title = $"Журнальный лот {Guid.NewGuid():N}"[..40];
        var listingId = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview, title: title);

        Assert.Equal(HttpStatusCode.OK,
            (await mod.PostAsync($"/api/moderation/listings/{listingId}/approve", null)).StatusCode);

        using var doc = await GetJson(mod, $"/api/moderation/logs?q={listingId}");
        var item = Assert.Single(Items(doc));

        Assert.Equal("listing.approve", item.GetProperty("action").GetString());
        Assert.Equal(title, item.GetProperty("target").GetProperty("label").GetString());
        Assert.Equal("Moderator", item.GetProperty("actor").GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Number, item.GetProperty("waitSeconds").ValueKind);
        Assert.False(item.GetProperty("payload").GetProperty("postModeration").GetBoolean());
    }

    [Fact]
    public async Task Log_filters_by_action_and_searches_listing_title()
    {
        var (mod, _) = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var marker = Guid.NewGuid().ToString("N")[..10];
        var approved = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview, title: $"Одобряемый {marker}");
        var rejected = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview, title: $"Отклоняемый {marker}");

        await mod.PostAsync($"/api/moderation/listings/{approved}/approve", null);
        await mod.PostAsJsonAsync($"/api/moderation/listings/{rejected}/reject",
            new { reason = "BadPhotos", comment = (string?)null });

        using var doc = await GetJson(mod, $"/api/moderation/logs?q={marker}&action=listing.reject");
        var ids = Items(doc).Select(i => i.GetProperty("targetId").GetGuid()).ToList();

        Assert.Contains(rejected, ids);
        Assert.DoesNotContain(approved, ids);
    }

    [Fact]
    public async Task Log_paginates_with_cursor_without_overlap()
    {
        var (mod, modId) = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        for (var i = 0; i < 3; i++)
        {
            var l = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview);
            await mod.PostAsync($"/api/moderation/listings/{l}/approve", null);
        }

        using var first = await GetJson(mod, $"/api/moderation/logs?actorId={modId}&limit=2");
        var firstIds = Items(first).Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(2, firstIds.Count);
        Assert.True(first.RootElement.GetProperty("hasMore").GetBoolean());
        var cursor = first.RootElement.GetProperty("nextCursor").GetString();

        using var second = await GetJson(mod, $"/api/moderation/logs?actorId={modId}&limit=2&cursor={cursor}");
        var secondIds = Items(second).Select(i => i.GetProperty("id").GetGuid()).ToList();

        Assert.Single(secondIds);
        Assert.Empty(firstIds.Intersect(secondIds));
    }

    [Fact]
    public async Task Log_summary_counts_actions_of_actor_in_period()
    {
        var (mod, modId) = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var a = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview);
        var r = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview);
        await mod.PostAsync($"/api/moderation/listings/{a}/approve", null);
        await mod.PostAsJsonAsync($"/api/moderation/listings/{r}/reject", new { reason = "Duplicate" });

        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("O"));
        using var doc = await GetJson(mod, $"/api/moderation/logs/summary?actorId={modId}&from={from}");
        var root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("total").GetInt32());
        Assert.Equal(1, root.GetProperty("approvals").GetInt32());
        Assert.Equal(1, root.GetProperty("rejections").GetInt32());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("avgWaitSeconds").ValueKind);
    }

    [Fact]
    public async Task Resolving_report_with_long_resolution_does_not_break_the_log()
    {
        // Итог разбора допускает 1000 символов, а колонка причины журнала — 500 (CHECK).
        // Раньше длинный итог ронял закрытие жалобы ошибкой БД.
        var (mod, _) = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var listingId = await factory.SeedListingAsync(sellerId);
        var reportId = await factory.SeedReportAsync(listingId);

        var resp = await mod.PostAsJsonAsync($"/api/moderation/reports/{reportId}/resolve",
            new { status = "Resolved", resolution = new string('я', 900) });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, await factory.ModerationLogCountAsync(reportId, "report.resolve"));
    }

    // ---- пользователи ----

    [Fact]
    public async Task Users_search_by_public_code_returns_row_without_contacts()
    {
        var (mod, _) = await Moderator();
        var userId = await factory.SeedUserAsync(Unique("findme"), Password);
        var code = await PublicCodeOf(mod, userId);

        using var doc = await GetJson(mod, $"/api/moderation/users?q={code}");
        var row = Assert.Single(Items(doc));

        Assert.Equal(userId, row.GetProperty("id").GetGuid());
        Assert.False(row.TryGetProperty("email", out _));
        Assert.False(row.TryGetProperty("phoneE164", out _));
    }

    [Fact]
    public async Task Users_banned_tab_and_summary_reflect_ban()
    {
        var (mod, _) = await Moderator();
        var userId = await factory.SeedUserAsync(Unique("tobeban"), Password);

        var ban = await mod.PostAsJsonAsync($"/api/moderation/users/{userId}/ban",
            new { reason = "Мошенничество", until = (DateTimeOffset?)null });
        Assert.Equal(HttpStatusCode.OK, ban.StatusCode);

        var code = await PublicCodeOf(mod, userId);
        using var doc = await GetJson(mod, $"/api/moderation/users?tab=Banned&q={code}");
        Assert.True(Assert.Single(Items(doc)).GetProperty("isBanned").GetBoolean());

        using var summary = await GetJson(mod, "/api/moderation/users/summary");
        Assert.True(summary.RootElement.GetProperty("permanentBans").GetInt32() >= 1);
        Assert.True(summary.RootElement.GetProperty("total").GetInt32() >= 1);
    }

    [Fact]
    public async Task Dossier_shows_sanctions_and_accounts_from_the_same_network()
    {
        var (mod, _) = await Moderator();
        var emailA = Unique("netA");
        var emailB = Unique("netB");
        var a = await factory.SeedUserAsync(emailA, Password);
        var b = await factory.SeedUserAsync(emailB, Password);

        // Оба входят через один и тот же тестовый сервер — один HMAC адреса.
        await AuthedClient(emailA);
        await AuthedClient(emailB);

        await mod.PostAsJsonAsync($"/api/moderation/users/{a}/ban",
            new { reason = "Проверка досье", until = DateTimeOffset.UtcNow.AddDays(3) });

        using var doc = await GetJson(mod, $"/api/moderation/users/{a}/dossier");
        var root = doc.RootElement;

        Assert.Contains(root.GetProperty("linkedAccounts").EnumerateArray(),
            x => x.GetProperty("id").GetGuid() == b);
        Assert.True(root.GetProperty("user").GetProperty("sharedNetworkAccounts").GetInt32() >= 1);

        var sanction = Assert.Single(root.GetProperty("sanctions").EnumerateArray());
        Assert.Equal("user.ban", sanction.GetProperty("action").GetString());
        Assert.Equal(JsonValueKind.String, sanction.GetProperty("until").ValueKind);
        Assert.False(root.GetProperty("user").TryGetProperty("email", out _));
    }

    // ---- жалобы ----

    [Fact]
    public async Task Reports_urgent_sort_puts_fraud_before_spam()
    {
        var (mod, _) = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var listingId = await factory.SeedListingAsync(sellerId);

        // Спам подан раньше — но мошенничество срочнее.
        var spam = await factory.SeedReportAsync(listingId, reason: ReportReason.Spam);
        var fraud = await factory.SeedReportAsync(listingId, reason: ReportReason.Fraud);

        using var doc = await GetJson(mod, $"/api/moderation/reports?q={listingId}&sort=Urgent");
        var ids = Items(doc).Select(i => i.GetProperty("id").GetGuid()).ToList();

        Assert.True(ids.IndexOf(fraud) < ids.IndexOf(spam));
        var fraudRow = Items(doc).First(i => i.GetProperty("id").GetGuid() == fraud);
        Assert.Equal(3, fraudRow.GetProperty("severity").GetInt32());
        Assert.Equal("listing", fraudRow.GetProperty("target").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Take_assigns_report_and_blocks_second_moderator_unless_forced()
    {
        var (mod1, mod1Id) = await Moderator();
        var (mod2, _) = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var listingId = await factory.SeedListingAsync(sellerId);
        var reportId = await factory.SeedReportAsync(listingId, reason: ReportReason.Fraud);

        Assert.Equal(HttpStatusCode.OK, (await mod1.PostAsync($"/api/moderation/reports/{reportId}/take", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await mod2.PostAsync($"/api/moderation/reports/{reportId}/take", null)).StatusCode);

        using (var doc = await GetJson(mod1, $"/api/moderation/reports/{reportId}"))
        {
            var report = doc.RootElement.GetProperty("report");
            Assert.Equal("InReview", report.GetProperty("status").GetString());
            Assert.Equal(mod1Id, report.GetProperty("assignee").GetProperty("id").GetGuid());
        }

        Assert.Equal(HttpStatusCode.OK,
            (await mod2.PostAsync($"/api/moderation/reports/{reportId}/take?force=true", null)).StatusCode);
        Assert.Equal(2, await factory.ModerationLogCountAsync(reportId, "report.take"));

        // Взятая в работу жалоба видна во вкладке «В работе» и не видна в «Новых».
        using var inReview = await GetJson(mod2, $"/api/moderation/reports?tab=InReview&q={reportId}");
        Assert.Single(Items(inReview));
        using var fresh = await GetJson(mod2, $"/api/moderation/reports?tab=New&q={reportId}");
        Assert.Empty(Items(fresh));
    }

    [Fact]
    public async Task Take_closed_report_returns_409()
    {
        var (mod, _) = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var listingId = await factory.SeedListingAsync(sellerId);
        var reportId = await factory.SeedReportAsync(listingId);
        await mod.PostAsJsonAsync($"/api/moderation/reports/{reportId}/resolve", new { status = "Rejected" });

        var resp = await mod.PostAsync($"/api/moderation/reports/{reportId}/take", null);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Report_detail_has_listing_snapshot_owner_and_reporter_stats()
    {
        var (mod, _) = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var reporterId = await factory.SeedUserAsync(Unique("reporter"), Password);
        var listingId = await factory.SeedListingAsync(sellerId,
            description: "Звоните +37377712345, предоплата на карту");

        var earlier = await factory.SeedReportAsync(listingId, reporterId: reporterId);
        await mod.PostAsJsonAsync($"/api/moderation/reports/{earlier}/resolve", new { status = "Rejected" });
        var reportId = await factory.SeedReportAsync(listingId, reason: ReportReason.Fraud, reporterId: reporterId);
        await factory.SeedReportAsync(listingId, reason: ReportReason.Spam);

        using var doc = await GetJson(mod, $"/api/moderation/reports/{reportId}");
        var root = doc.RootElement;

        var listing = root.GetProperty("listing");
        Assert.Equal(listingId, listing.GetProperty("id").GetGuid());
        // Модератору — сырой текст, без редактирования контактов.
        Assert.Contains("+37377712345", listing.GetProperty("description").GetString());
        Assert.Equal(sellerId, listing.GetProperty("owner").GetProperty("id").GetGuid());

        var stats = root.GetProperty("reporterStats");
        Assert.Equal(2, stats.GetProperty("reportsTotal").GetInt32());
        Assert.Equal(1, stats.GetProperty("reportsRejected").GetInt32());

        Assert.Equal(1, root.GetProperty("otherOpenReportsOnTarget").GetInt32());
    }

    [Fact]
    public async Task Reports_summary_counts_statuses()
    {
        var (mod, _) = await Moderator();
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var listingId = await factory.SeedListingAsync(sellerId);
        var reportId = await factory.SeedReportAsync(listingId);
        await mod.PostAsJsonAsync($"/api/moderation/reports/{reportId}/resolve", new { status = "Resolved" });

        using var doc = await GetJson(mod, "/api/moderation/reports/summary");
        var root = doc.RootElement;

        Assert.True(root.GetProperty("closed").GetInt32() >= 1);
        Assert.Equal(JsonValueKind.Number, root.GetProperty("avgReactionMinutes").ValueKind);
        Assert.Equal(
            root.GetProperty("new").GetInt32() + root.GetProperty("inReview").GetInt32() + root.GetProperty("closed").GetInt32(),
            root.GetProperty("all").GetInt32());
    }

    // ---- helpers ----

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";

    private async Task<(HttpClient Client, Guid Id)> Moderator()
    {
        var email = Unique("mod");
        var id = await factory.SeedUserAsync(email, Password, role: UserRole.Moderator);
        return (await AuthedClient(email), id);
    }

    private async Task<HttpClient> AuthedClient(string email)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new("Bearer", doc.RootElement.GetProperty("accessToken").GetString());
        return client;
    }

    /// <summary>«ID профиля» — через карточку контактов модератора (единственное, где он есть по GUID).</summary>
    private static async Task<string> PublicCodeOf(HttpClient mod, Guid userId)
    {
        using var doc = await GetJson(mod, $"/api/moderation/users/{userId}");
        return doc.RootElement.GetProperty("publicCode").GetString()!;
    }

    private static async Task<JsonDocument> GetJson(HttpClient client, string url)
    {
        var resp = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    }

    private static List<JsonElement> Items(JsonDocument doc) =>
        doc.RootElement.GetProperty("items").EnumerateArray().ToList();
}
