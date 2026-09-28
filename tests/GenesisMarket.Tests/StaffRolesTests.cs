using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Api.Cli;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Служебные роли: смена ролей владельцем (правила, журнал, отзыв сессий), конфликт
/// интересов модератора, иерархия санкций, неизменяемость журналов и CLI grant-role.
/// </summary>
[Collection(StaffRolesCollection.Name)]
public class StaffRolesTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    // ---- Смена роли владельцем ----

    [Fact]
    public async Task Owner_promotes_user_logs_it_and_ends_target_sessions()
    {
        var (owner, ownerId) = await Staff(UserRole.Owner);
        var targetEmail = Unique("promote");
        var targetId = await factory.SeedUserAsync(targetEmail, Password);
        var (targetClient, targetRefresh) = await LoginWithRefresh(targetEmail);

        var resp = await owner.PutAsJsonAsync($"/api/admin/staff/{targetId}/role",
            new { role = "Moderator", expectedRole = "User", reason = "Новый модератор" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using (var scope = Db(out var db))
        {
            var assignment = await db.ProjectRoles.SingleAsync(a => a.UserId == targetId);
            Assert.Equal(UserRole.Moderator, assignment.Role);
            Assert.Equal(ownerId, assignment.GrantedByUserId);

            var change = await db.RoleChanges.SingleAsync(c => c.UserId == targetId);
            Assert.Equal(("User", "Moderator", "ui"), (change.OldRole, change.NewRole, change.Source));
            Assert.Equal(ownerId, change.ActorId);

            var audit = await db.AuditEvents.SingleAsync(e => e.Id == change.AuditEventId);
            Assert.Equal("role.change", audit.Action);
            Assert.Contains("User", audit.OldValue);
            Assert.Contains("Moderator", audit.NewValue);
            Assert.Equal("Новый модератор", audit.Reason);
        }

        // Старая сессия цели мертва: и access (SecurityStamp), и refresh (отозван).
        Assert.Equal(HttpStatusCode.Unauthorized, (await targetClient.GetAsync("/api/me")).StatusCode);
        var refresh = await factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new { refreshToken = targetRefresh });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        // Новый вход без 2FA даёт сессию, но служебный раздел закрыт до настройки 2FA.
        var fresh = await AuthedClient(targetEmail);
        var queue = await fresh.GetAsync("/api/moderation/queue");
        Assert.Equal(HttpStatusCode.Forbidden, queue.StatusCode);
        Assert.Contains("mfa_setup_required", await queue.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Owner_cannot_demote_self_or_assign_owner()
    {
        var (owner, ownerId) = await Staff(UserRole.Owner);
        var userId = await factory.SeedUserAsync(Unique("plain"), Password);

        var self = await owner.PutAsJsonAsync($"/api/admin/staff/{ownerId}/role",
            new { role = "Admin", expectedRole = "Owner" });
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);

        var makeOwner = await owner.PutAsJsonAsync($"/api/admin/staff/{userId}/role",
            new { role = "Owner", expectedRole = "User" });
        Assert.Equal(HttpStatusCode.Forbidden, makeOwner.StatusCode);

        await using var scope = Db(out var db);
        Assert.Equal(UserRole.Owner, (await db.ProjectRoles.SingleAsync(a => a.UserId == ownerId)).Role);
        Assert.False(await db.ProjectRoles.AnyAsync(a => a.UserId == userId));
    }

    [Fact]
    public async Task Stale_expected_role_is_rejected_without_change()
    {
        var (owner, _) = await Staff(UserRole.Owner);
        var targetId = await factory.SeedUserAsync(Unique("stale"), Password, role: UserRole.Moderator);

        // Диалог показывал «User», а человек уже модератор.
        var resp = await owner.PutAsJsonAsync($"/api/admin/staff/{targetId}/role",
            new { role = "Admin", expectedRole = "User" });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        await using var scope = Db(out var db);
        Assert.Equal(UserRole.Moderator, (await db.ProjectRoles.SingleAsync(a => a.UserId == targetId)).Role);
    }

    [Fact]
    public async Task Owner_demotes_staff_to_user_and_staff_list_reflects_it()
    {
        var (owner, _) = await Staff(UserRole.Owner);
        var targetId = await factory.SeedUserAsync(Unique("demote"), Password, role: UserRole.Admin);

        using (var list = JsonDocument.Parse(await owner.GetStringAsync("/api/admin/staff")))
            Assert.Contains(list.RootElement.EnumerateArray(), s => s.GetProperty("userId").GetGuid() == targetId);

        var resp = await owner.PutAsJsonAsync($"/api/admin/staff/{targetId}/role",
            new { role = "User", expectedRole = "Admin" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using (var list = JsonDocument.Parse(await owner.GetStringAsync("/api/admin/staff")))
            Assert.DoesNotContain(list.RootElement.EnumerateArray(), s => s.GetProperty("userId").GetGuid() == targetId);
    }

    [Fact]
    public async Task Email_lookup_finds_account_and_is_journaled()
    {
        var (owner, ownerId) = await Staff(UserRole.Owner);
        var email = Unique("lookup");
        var userId = await factory.SeedUserAsync(email, Password);

        using var doc = JsonDocument.Parse(await owner.GetStringAsync($"/api/admin/staff/lookup?email={Uri.EscapeDataString(email.ToUpperInvariant())}"));
        Assert.Equal(userId, doc.RootElement.GetProperty("userId").GetGuid());
        Assert.Equal("User", doc.RootElement.GetProperty("role").GetString());

        await using var scope = Db(out var db);
        var entry = await db.AuditEvents.SingleAsync(e =>
            e.Action == "staff.lookup_by_email" && e.ActorId == ownerId && e.TargetId == userId.ToString());
        Assert.DoesNotContain(email, entry.NewValue ?? "");
    }

    // ---- Конфликт интересов модератора ----

    [Fact]
    public async Task Moderator_cannot_decide_or_assign_own_listing()
    {
        var (mod, modId) = await Staff(UserRole.Moderator);
        var own = await factory.SeedListingAsync(modId, ListingStatus.PendingReview);

        Assert.Equal(HttpStatusCode.Forbidden, (await mod.PostAsync($"/api/moderation/listings/{own}/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mod.PostAsync($"/api/moderation/listings/{own}/assign", null)).StatusCode);

        var bulk = await mod.PostAsJsonAsync("/api/moderation/listings/bulk/approve", new { ids = new[] { own } });
        Assert.Equal(HttpStatusCode.OK, bulk.StatusCode);
        using var doc = JsonDocument.Parse(await bulk.Content.ReadAsStringAsync());
        Assert.Empty(doc.RootElement.GetProperty("succeeded").EnumerateArray());
        Assert.Single(doc.RootElement.GetProperty("failed").EnumerateArray());

        Assert.Equal("PendingReview", (await factory.ListingModerationAsync(own)).Status);
    }

    [Fact]
    public async Task Owner_is_not_exempt_from_own_listing_rule()
    {
        var (owner, ownerId) = await Staff(UserRole.Owner);
        var own = await factory.SeedListingAsync(ownerId, ListingStatus.PendingReview);

        var resp = await owner.PostAsJsonAsync($"/api/moderation/listings/{own}/reject",
            new { reason = "Duplicate" });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Moderator_cannot_handle_report_about_own_listing()
    {
        var (mod, modId) = await Staff(UserRole.Moderator);
        var own = await factory.SeedListingAsync(modId);
        var reporter = await AuthedClient(await SeedPlain("reporter"));

        var report = await reporter.PostAsJsonAsync("/api/reports",
            new { targetType = "Listing", targetId = own, reason = "Spam", comment = "спам" });
        Assert.True(report.IsSuccessStatusCode, await report.Content.ReadAsStringAsync());
        using var created = JsonDocument.Parse(await report.Content.ReadAsStringAsync());
        var reportId = created.RootElement.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await mod.PostAsync($"/api/moderation/reports/{reportId}/take", null)).StatusCode);
        var resolve = await mod.PostAsJsonAsync($"/api/moderation/reports/{reportId}/resolve",
            new { status = "Rejected", resolution = "Всё в порядке" });
        Assert.Equal(HttpStatusCode.Forbidden, resolve.StatusCode);
    }

    // ---- Иерархия санкций ----

    [Fact]
    public async Task Sanctions_apply_only_to_lower_roles()
    {
        var (mod, _) = await Staff(UserRole.Moderator);
        var (admin, _) = await Staff(UserRole.Admin);
        var otherModId = await factory.SeedUserAsync(Unique("othermod"), Password, role: UserRole.Moderator);
        var otherAdminId = await factory.SeedUserAsync(Unique("otheradmin"), Password, role: UserRole.Admin);

        // Модератор не предупреждает модератора; админ — может.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await mod.PostAsJsonAsync($"/api/moderation/users/{otherModId}/warn", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PostAsJsonAsync($"/api/moderation/users/{otherModId}/warn", new { reason = "x" })).StatusCode);

        // Админ не банит админа; модератора — может.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.PostAsJsonAsync($"/api/admin/users/{otherAdminId}/ban", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PostAsJsonAsync($"/api/admin/users/{otherModId}/ban", new { reason = "x" })).StatusCode);
    }

    [Fact]
    public async Task Ban_is_journaled_with_old_and_new_state()
    {
        var (admin, adminId) = await Staff(UserRole.Admin);
        var victimId = await factory.SeedUserAsync(Unique("victim"), Password);

        var ban = await admin.PostAsJsonAsync($"/api/admin/users/{victimId}/ban", new { reason = "Мошенничество" });
        Assert.Equal(HttpStatusCode.OK, ban.StatusCode);

        await using var scope = Db(out var db);
        var entry = await db.AuditEvents.SingleAsync(e => e.Action == "user.ban" && e.TargetId == victimId.ToString());
        Assert.Equal(adminId, entry.ActorId);
        // jsonb нормализует пробелы — сравниваем разобранные значения, а не строки.
        using var before = JsonDocument.Parse(entry.OldValue!);
        using var after = JsonDocument.Parse(entry.NewValue!);
        Assert.False(before.RootElement.GetProperty("isBanned").GetBoolean());
        Assert.True(after.RootElement.GetProperty("isBanned").GetBoolean());
    }

    // ---- Неизменяемость журналов ----

    [Fact]
    public async Task Journals_reject_update_and_delete_at_database_level()
    {
        var (owner, _) = await Staff(UserRole.Owner);
        var targetId = await factory.SeedUserAsync(Unique("immut"), Password);
        await owner.PutAsJsonAsync($"/api/admin/staff/{targetId}/role", new { role = "Moderator", expectedRole = "User" });

        await using var scope = Db(out var db);
        var target = targetId.ToString();

        var update = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"UPDATE audit_events SET \"Reason\" = 'подмена' WHERE \"TargetId\" = {target}"));
        Assert.Contains("только на добавление", update.MessageText);

        await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM role_changes WHERE \"UserId\" = {targetId}"));

        // Снятие IpHash по сроку хранения — единственное разрешённое изменение.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE audit_events SET \"IpHash\" = NULL WHERE \"TargetId\" = {target}");
    }

    // ---- CLI ----

    [Fact]
    public async Task Cli_grant_role_assigns_owner_once_and_transfers_only_explicitly()
    {
        var first = Unique("cli-owner");
        var second = Unique("cli-next");
        var firstId = await factory.SeedUserAsync(first, Password);
        var secondId = await factory.SeedUserAsync(second, Password);

        // Прежний сидированный владелец (из других тестов класса) не должен мешать: снимаем.
        await using (var scope = Db(out var db))
            await db.ProjectRoles.Where(a => a.Role == UserRole.Owner).ExecuteDeleteAsync();

        Assert.Equal(0, await AdminCli.RunAsync(["grant-role", first, "Owner", "--yes", "--reason", "Первичная настройка"]));
        Assert.Equal(1, await AdminCli.RunAsync(["grant-role", second, "Owner", "--yes"]));
        Assert.Equal(0, await AdminCli.RunAsync(["grant-role", second, "owner", "--transfer", "--yes"]));

        await using (var scope = Db(out var db))
        {
            Assert.Equal(UserRole.Admin, (await db.ProjectRoles.SingleAsync(a => a.UserId == firstId)).Role);
            Assert.Equal(UserRole.Owner, (await db.ProjectRoles.SingleAsync(a => a.UserId == secondId)).Role);

            var changes = await db.RoleChanges.Where(c => c.UserId == firstId || c.UserId == secondId)
                .OrderBy(c => c.OccurredAt).ToListAsync();
            Assert.All(changes, c => Assert.Equal("cli", c.Source));
            Assert.All(changes, c => Assert.Null(c.ActorId));
            Assert.All(changes, c => Assert.False(string.IsNullOrEmpty(c.ActorLabel)));
            Assert.Equal(3, changes.Count); // first: User→Owner, first: Owner→Admin, second: User→Owner
            Assert.Equal("Первичная настройка", changes[0].Reason);

            var cliAudit = await db.AuditEvents.CountAsync(e => e.ActorKind == "cli" && e.Action == "role.change" &&
                (e.TargetId == firstId.ToString() || e.TargetId == secondId.ToString()));
            Assert.Equal(3, cliAudit);
        }
    }

    [Fact]
    public async Task Cli_rejects_unknown_user_and_bad_role()
    {
        Assert.Equal(1, await AdminCli.RunAsync(["grant-role", Unique("nobody"), "Admin", "--yes"]));
        Assert.Equal(2, await AdminCli.RunAsync(["grant-role", Unique("x"), "Superuser", "--yes"]));
        Assert.Equal(2, await AdminCli.RunAsync(["grant-role"]));
    }

    [Fact]
    public async Task Cli_reset_2fa_removes_second_factor_and_ends_sessions()
    {
        var email = Unique("lostphone");
        var id = await factory.SeedUserAsync(email, Password, role: UserRole.Moderator);
        var (client, _) = await LoginWithRefresh(email);

        Assert.Equal(0, await AdminCli.RunAsync(["reset-2fa", email, "--yes", "--reason", "Потерян телефон"]));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
        await using var scope = Db(out var db);
        Assert.False(await db.TwoFactors.AnyAsync(t => t.UserId == id));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "2fa.reset" && e.TargetId == id.ToString() && e.ActorKind == "cli"));
    }

    // ---- helpers ----

    private AsyncServiceScope Db(out AppDbContext db)
    {
        var scope = factory.Services.CreateAsyncScope();
        db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return scope;
    }

    private async Task<(HttpClient Client, Guid Id)> Staff(UserRole role)
    {
        var email = Unique(role.ToString().ToLowerInvariant());
        var id = await factory.SeedUserAsync(email, Password, role: role);
        return (await AuthedClient(email), id);
    }

    private async Task<string> SeedPlain(string prefix)
    {
        var email = Unique(prefix);
        await factory.SeedUserAsync(email, Password);
        return email;
    }

    private async Task<HttpClient> AuthedClient(string email) => (await LoginWithRefresh(email)).Client;

    private async Task<(HttpClient Client, string RefreshToken)> LoginWithRefresh(string email)
    {
        var client = factory.CreateClient();
        var login = await factory.LoginAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new("Bearer", doc.RootElement.GetProperty("accessToken").GetString());
        return (client, doc.RootElement.GetProperty("refreshToken").GetString()!);
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";
}

/// <summary>
/// CLI строит собственный хост из переменных окружения процесса (строка подключения
/// тестового контейнера). Коллекция без параллелизма: иначе CLI-тест мог бы прочитать
/// строку подключения, которую успела переписать фабрика другого класса.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StaffRolesCollection
{
    public const string Name = "staff-roles (CLI через переменные окружения)";
}
