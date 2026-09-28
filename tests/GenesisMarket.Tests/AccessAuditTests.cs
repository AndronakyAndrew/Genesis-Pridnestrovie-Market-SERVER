using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Api.Auth.TwoFactor;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Исправления по аудиту доступа и 2FA (2026-09-28): контакты модератору только в рамках
/// жалобы, разбан по рангу, жалоба от самого сотрудника, журнал скрытия объявлений и
/// отзывов, прежний статус в решениях, блокировка второго фактора по аккаунту, вход
/// заблокированного и обычный вход пользователя без 2FA.
/// </summary>
public class AccessAuditTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    // ---- Контакты ----

    [Fact]
    public async Task Moderator_sees_contacts_only_of_participants_of_own_taken_report()
    {
        var (mod, _) = await Staff(UserRole.Moderator);
        var ownerId = await factory.SeedUserAsync(Unique("offender"), Password);
        var reporterId = await factory.SeedUserAsync(Unique("reporter"), Password);
        var outsiderId = await factory.SeedUserAsync(Unique("outsider"), Password);
        var listingId = await factory.SeedListingAsync(ownerId);
        var reportId = await factory.SeedReportAsync(listingId, reporterId: reporterId);

        // Без жалобы — нельзя, и в журнал ничего не пишется.
        Assert.Equal(HttpStatusCode.Forbidden, (await mod.GetAsync($"/api/moderation/users/{ownerId}")).StatusCode);
        // Жалоба есть, но ещё не взята в работу этим модератором.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await mod.GetAsync($"/api/moderation/users/{ownerId}?reportId={reportId}")).StatusCode);
        Assert.Equal(0, await factory.ModerationLogCountAsync(ownerId, ModerationLog.ActionViewUserContacts));

        Assert.Equal(HttpStatusCode.OK, (await mod.PostAsync($"/api/moderation/reports/{reportId}/take", null)).StatusCode);

        // Участники взятой жалобы — владелец объявления и автор жалобы.
        Assert.Equal(HttpStatusCode.OK, (await mod.GetAsync($"/api/moderation/users/{ownerId}?reportId={reportId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mod.GetAsync($"/api/moderation/users/{reporterId}?reportId={reportId}")).StatusCode);
        // Посторонний — нет, даже с действующей жалобой.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await mod.GetAsync($"/api/moderation/users/{outsiderId}?reportId={reportId}")).StatusCode);

        Assert.Equal(1, await factory.ModerationLogCountAsync(ownerId, ModerationLog.ActionViewUserContacts));

        // Другой модератор, не взявший жалобу, — нет.
        var (otherMod, _) = await Staff(UserRole.Moderator);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await otherMod.GetAsync($"/api/moderation/users/{ownerId}?reportId={reportId}")).StatusCode);
    }

    [Fact]
    public async Task Admin_sees_any_contacts_with_journal()
    {
        var (admin, _) = await Staff(UserRole.Admin);
        var userId = await factory.SeedUserAsync(Unique("any"), Password);

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/moderation/users/{userId}")).StatusCode);
        Assert.Equal(1, await factory.ModerationLogCountAsync(userId, ModerationLog.ActionViewUserContacts));
    }

    // ---- Модератор после переноса прав к Admin ----

    [Fact]
    public async Task Moderator_keeps_queue_counters_and_warning_but_not_admin_sections()
    {
        var (mod, _) = await Staff(UserRole.Moderator);
        var userId = await factory.SeedUserAsync(Unique("warn-target"), Password);

        // Остаётся: счётчики очереди (бейджи меню) и предупреждение.
        Assert.Equal(HttpStatusCode.OK, (await mod.GetAsync("/api/moderation/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await mod.PostAsJsonAsync($"/api/moderation/users/{userId}/warn", new { reason = "Спам" })).StatusCode);

        // Уровень Admin: реестр, досье, журнал, карты, заявки бизнеса.
        foreach (var url in new[]
                 {
                     "/api/moderation/users", "/api/moderation/users/summary", $"/api/moderation/users/{userId}/dossier",
                     "/api/moderation/logs", "/api/moderation/blocked-cards", "/api/moderation/business"
                 })
            Assert.Equal(HttpStatusCode.Forbidden, (await mod.GetAsync(url)).StatusCode);
    }

    // ---- Разбан по рангу ----

    [Fact]
    public async Task Admin_cannot_unban_admin_banned_by_owner()
    {
        var (owner, _) = await Staff(UserRole.Owner);
        var (admin, _) = await Staff(UserRole.Admin);
        var victimId = await factory.SeedUserAsync(Unique("banned-admin"), Password, role: UserRole.Admin);

        Assert.Equal(HttpStatusCode.OK,
            (await owner.PostAsJsonAsync($"/api/admin/users/{victimId}/ban", new { reason = "Проверка" })).StatusCode);

        var unban = await admin.PostAsync($"/api/admin/users/{victimId}/unban", null);
        Assert.Equal(HttpStatusCode.Forbidden, unban.StatusCode);
        Assert.True((await factory.UserBanStateAsync(victimId)).IsBanned);

        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/admin/users/{victimId}/unban", null)).StatusCode);
        Assert.False((await factory.UserBanStateAsync(victimId)).IsBanned);
    }

    // ---- Жалоба, поданная самим сотрудником ----

    [Fact]
    public async Task Staff_cannot_take_or_resolve_report_they_filed()
    {
        var (mod, modId) = await Staff(UserRole.Moderator);
        var ownerId = await factory.SeedUserAsync(Unique("target"), Password);
        var listingId = await factory.SeedListingAsync(ownerId);
        var reportId = await factory.SeedReportAsync(listingId, reporterId: modId);

        Assert.Equal(HttpStatusCode.Forbidden, (await mod.PostAsync($"/api/moderation/reports/{reportId}/take", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await mod.PostAsJsonAsync($"/api/moderation/reports/{reportId}/resolve", new { status = "Resolved" })).StatusCode);
    }

    // ---- Журнал ----

    [Fact]
    public async Task Moderator_hiding_foreign_listing_is_journaled_but_owner_archiving_is_not()
    {
        var (mod, _) = await Staff(UserRole.Moderator);
        var ownerEmail = Unique("hide-owner");
        var ownerId = await factory.SeedUserAsync(ownerEmail, Password);
        var foreign = await factory.SeedListingAsync(ownerId);
        var own = await factory.SeedListingAsync(ownerId, title: "Своё объявление владельца");

        Assert.Equal(HttpStatusCode.NoContent,
            (await mod.DeleteAsync($"/api/listings/{foreign}?reason={Uri.EscapeDataString("Запрещённый товар")}")).StatusCode);
        Assert.Equal(1, await factory.ModerationLogCountAsync(foreign, ModerationLog.ActionHideListing));

        var owner = await Client(ownerEmail);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/listings/{own}")).StatusCode);
        Assert.Equal(0, await factory.ModerationLogCountAsync(own, ModerationLog.ActionHideListing));

        await using var scope = Db(out var db);
        var entry = await db.AuditEvents.SingleAsync(e => e.Action == ModerationLog.ActionHideListing && e.TargetId == foreign.ToString());
        Assert.Contains("Active", entry.OldValue);
        Assert.Equal("Запрещённый товар", entry.Reason);
    }

    [Fact]
    public async Task Review_hide_is_journaled()
    {
        var (mod, _) = await Staff(UserRole.Moderator);
        var sellerId = await factory.SeedUserAsync(Unique("seller"), Password);
        var authorId = await factory.SeedUserAsync(Unique("author"), Password);
        var listingId = await factory.SeedListingAsync(sellerId);

        Guid reviewId;
        await using (var scope = Db(out var db))
        {
            var review = new Review
            {
                ListingId = listingId, AuthorId = authorId, TargetUserId = sellerId,
                Rating = 1, Text = "Отзыв для проверки журнала модерации"
            };
            db.Reviews.Add(review);
            await db.SaveChangesAsync();
            reviewId = review.Id;
        }

        Assert.Equal(HttpStatusCode.NoContent, (await mod.PostAsync($"/api/reviews/{reviewId}/hide", null)).StatusCode);
        Assert.Equal(1, await factory.ModerationLogCountAsync(reviewId, ModerationLog.ActionHideReview));
    }

    [Fact]
    public async Task Listing_decision_journal_keeps_previous_status()
    {
        var (mod, _) = await Staff(UserRole.Moderator);
        var ownerId = await factory.SeedUserAsync(Unique("decision-owner"), Password);
        var listingId = await factory.SeedListingAsync(ownerId, status: ListingStatus.PendingReview);

        Assert.Equal(HttpStatusCode.OK, (await mod.PostAsync($"/api/moderation/listings/{listingId}/approve", null)).StatusCode);

        await using var scope = Db(out var db);
        var entry = await db.AuditEvents.SingleAsync(e => e.Action == ModerationLog.ActionApproveListing && e.TargetId == listingId.ToString());
        Assert.Contains("PendingReview", entry.OldValue);
        Assert.Contains("Active", entry.NewValue);
    }

    // ---- 2FA ----

    [Fact]
    public async Task Second_factor_is_locked_per_account_after_threshold()
    {
        var email = Unique("lockout");
        var userId = await factory.SeedUserAsync(email, Password, twoFactor: true);
        var threshold = new TwoFactorOptions().LockoutThreshold;
        var perTicket = new TwoFactorOptions().ChallengeMaxAttempts;

        // Неверные коды через несколько билетов (как с разных адресов): счётчик — по аккаунту.
        var wrong = 0;
        while (wrong < threshold)
        {
            var ticket = await Challenge(email);
            for (var i = 0; i < perTicket && wrong < threshold; i++, wrong++)
                await factory.CreateClient().PostAsJsonAsync("/api/auth/2fa/verify",
                    new { challengeToken = ticket, code = "000000" });
        }

        // Даже верный код теперь не принимается — 429, сессии нет.
        var locked = await factory.CreateClient().PostAsJsonAsync("/api/auth/2fa/verify",
            new { challengeToken = await Challenge(email), code = await factory.TotpCodeAsync(email) });
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.DoesNotContain("accessToken", await locked.Content.ReadAsStringAsync());

        await using var scope = Db(out var db);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "2fa.locked" && e.TargetId == userId.ToString()));
        Assert.NotNull((await db.TwoFactors.SingleAsync(t => t.UserId == userId)).LockedUntil);
    }

    [Fact]
    public async Task Banned_user_is_refused_at_login()
    {
        var email = Unique("banned-login");
        await factory.SeedUserAsync(email, Password, banned: true);

        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.DoesNotContain("accessToken", await login.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Regular_user_without_2fa_signs_in_by_password_and_by_code()
    {
        var email = Unique("plain-user");
        await factory.SeedUserAsync(email, Password);

        // Пароль — сразу сессия, без второго шага.
        var byPassword = await Client(email);
        Assert.Equal(HttpStatusCode.OK, (await byPassword.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await byPassword.GetAsync("/api/me/listings")).StatusCode);

        // Код на почту — тоже сразу 200, не 202.
        var anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anon.PostAsJsonAsync("/api/auth/login-code", new { email })).StatusCode);
        var code = factory.LastCode(email.ToLowerInvariant());
        Assert.NotNull(code);
        var byCode = await anon.PostAsJsonAsync("/api/auth/login-code/verify", new { email, code });
        Assert.Equal(HttpStatusCode.OK, byCode.StatusCode);
        Assert.Contains("accessToken", await byCode.Content.ReadAsStringAsync());

        // Служебные разделы обычному пользователю закрыты.
        Assert.Equal(HttpStatusCode.Forbidden, (await byPassword.GetAsync("/api/moderation/queue")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await byPassword.GetAsync("/api/admin/staff")).StatusCode);
    }

    // ---- helpers ----

    private async Task<string> Challenge(string email)
    {
        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.Accepted, login.StatusCode);
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("challengeToken").GetString()!;
    }

    private async Task<(HttpClient Client, Guid Id)> Staff(UserRole role)
    {
        var email = Unique(role.ToString().ToLowerInvariant());
        var id = await factory.SeedUserAsync(email, Password, role: role);
        return (await Client(email), id);
    }

    private async Task<HttpClient> Client(string email)
    {
        var client = factory.CreateClient();
        var login = await factory.LoginAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new("Bearer", doc.RootElement.GetProperty("accessToken").GetString());
        return client;
    }

    private AsyncServiceScope Db(out AppDbContext db)
    {
        var scope = factory.Services.CreateAsyncScope();
        db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return scope;
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";
}
