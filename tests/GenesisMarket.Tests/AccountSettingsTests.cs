using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Api.Accounts;
using GenesisMarket.Api.Outbox;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GenesisMarket.Tests;

public sealed class AccountSettingsTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    [Fact]
    public async Task Email_change_sends_to_new_email_and_revokes_other_sessions()
    {
        var oldEmail = Unique();
        var newEmail = Unique();
        var userId = await factory.SeedUserAsync(oldEmail, Password);
        var first = await Login(oldEmail);
        var other = await Login(oldEmail);
        var otherRefresh = other.Login.RootElement.GetProperty("refreshToken").GetString()!;

        var send = await first.Client.PostAsJsonAsync("/api/account/email-change/send-code", new { newEmail });
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        Assert.NotNull(factory.LastCode(newEmail));

        var verify = await first.Client.PostAsJsonAsync("/api/account/email-change/verify",
            new { newEmail, code = factory.LastCode(newEmail) });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        using var body = JsonDocument.Parse(await verify.Content.ReadAsStringAsync());
        Assert.Equal(newEmail, body.RootElement.GetProperty("email").GetString());
        first.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", body.RootElement.GetProperty("accessToken").GetString());
        Assert.NotNull(factory.LastEmail(oldEmail));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(newEmail, await db.Users.Where(u => u.Id == userId).Select(u => u.Email).SingleAsync());
        var oldRefresh = await factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new { refreshToken = otherRefresh });
        Assert.Equal(HttpStatusCode.Unauthorized, oldRefresh.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.Client.GetAsync("/api/me")).StatusCode);

        var codeClient = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await codeClient.PostAsJsonAsync("/api/auth/login-code", new { email = newEmail })).StatusCode);
        var loginCode = factory.LastCode(newEmail)!;
        Assert.Equal(HttpStatusCode.OK, (await codeClient.PostAsJsonAsync("/api/auth/login-code/verify", new { email = newEmail, code = loginCode })).StatusCode);
    }

    [Fact]
    public async Task Email_change_taken_address_is_neutral_and_does_not_send_code()
    {
        var ownerEmail = Unique();
        var takenEmail = Unique();
        await factory.SeedUserAsync(ownerEmail, Password);
        await factory.SeedUserAsync(takenEmail, Password);
        var owner = await Login(ownerEmail);
        var taken = await owner.Client.PostAsJsonAsync("/api/account/email-change/send-code", new { newEmail = takenEmail });
        var free = await owner.Client.PostAsJsonAsync("/api/account/email-change/send-code", new { newEmail = Unique() });
        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
        Assert.Equal(await taken.Content.ReadAsStringAsync(), await free.Content.ReadAsStringAsync());
        Assert.Null(factory.LastCode(takenEmail));
    }

    [Fact]
    public async Task Email_change_uses_existing_send_cooldown_and_attempt_limit()
    {
        var email = Unique();
        await factory.SeedUserAsync(email, Password);
        var client = await Login(email);
        var newEmail = Unique();
        Assert.Equal(HttpStatusCode.OK, (await client.Client.PostAsJsonAsync(
            "/api/account/email-change/send-code", new { newEmail })).StatusCode);

        var cooldown = await client.Client.PostAsJsonAsync(
            "/api/account/email-change/send-code", new { newEmail });
        Assert.Equal(HttpStatusCode.TooManyRequests, cooldown.StatusCode);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var invalid = await client.Client.PostAsJsonAsync(
                "/api/account/email-change/verify", new { newEmail, code = "999999" });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        var limited = await client.Client.PostAsJsonAsync(
            "/api/account/email-change/verify", new { newEmail, code = factory.LastCode(newEmail) });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Email_change_detects_address_taken_after_code_was_sent()
    {
        var email = Unique();
        var newEmail = Unique();
        await factory.SeedUserAsync(email, Password);
        var client = await Login(email);
        Assert.Equal(HttpStatusCode.OK, (await client.Client.PostAsJsonAsync(
            "/api/account/email-change/send-code", new { newEmail })).StatusCode);
        await factory.SeedUserAsync(newEmail, Password);

        var verify = await client.Client.PostAsJsonAsync("/api/account/email-change/verify",
            new { newEmail, code = factory.LastCode(newEmail) });
        Assert.Equal(HttpStatusCode.Conflict, verify.StatusCode);
        using var body = JsonDocument.Parse(await verify.Content.ReadAsStringAsync());
        Assert.Equal("email_already_in_use", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Concurrent_email_change_confirmation_consumes_code_once()
    {
        var email = Unique();
        var newEmail = Unique();
        await factory.SeedUserAsync(email, Password);
        var client = await Login(email);
        Assert.Equal(HttpStatusCode.OK, (await client.Client.PostAsJsonAsync(
            "/api/account/email-change/send-code", new { newEmail })).StatusCode);
        var code = factory.LastCode(newEmail);

        var attempts = await Task.WhenAll(
            client.Client.PostAsJsonAsync("/api/account/email-change/verify", new { newEmail, code }),
            client.Client.PostAsJsonAsync("/api/account/email-change/verify", new { newEmail, code }));
        Assert.Single(attempts, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(attempts, response => response.StatusCode != HttpStatusCode.OK);
    }

    [Fact]
    public async Task Notification_preferences_reject_security_and_unknown_group_and_apply_disable()
    {
        var email = Unique();
        await factory.SeedUserAsync(email, Password);
        var client = await Login(email);
        var security = await client.Client.PutAsJsonAsync("/api/account/notification-preferences", new { groups = new { security = false } });
        var unknown = await client.Client.PutAsJsonAsync("/api/account/notification-preferences", new { groups = new { mystery = false } });
        Assert.Equal(HttpStatusCode.BadRequest, security.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var update = await client.Client.PutAsJsonAsync("/api/account/notification-preferences", new { groups = new { reviews = false } });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using var json = JsonDocument.Parse(await update.Content.ReadAsStringAsync());
        var reviews = json.RootElement.GetProperty("groups").EnumerateArray()
            .Single(x => x.GetProperty("key").GetString() == "reviews");
        Assert.False(reviews.GetProperty("enabled").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        var notifier = scope.ServiceProvider.GetRequiredService<IUserNotifier>();
        await notifier.NotifyAsync(userId, NotificationGroups.Reviews, "Отзыв", "Проверка", default);
        Assert.Null(factory.LastEmail(email));
        await notifier.NotifyAsync(userId, NotificationGroups.Activity, "Объявление", "Проверка", default);
        Assert.NotNull(factory.LastEmail(email));
    }

    [Fact]
    public async Task Account_deletion_requires_code_on_new_and_legacy_routes_and_code_is_one_time()
    {
        var email = Unique();
        await factory.SeedUserAsync(email, Password);
        var client = await Login(email);
        var legacy = await client.Client.DeleteAsync("/api/me");
        Assert.Equal(HttpStatusCode.Conflict, legacy.StatusCode);
        var noCode = await client.Client.PostAsJsonAsync("/api/account/delete/confirm", new { code = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, noCode.StatusCode);
        await client.Client.PostAsync("/api/account/delete/send-code", null);
        var code = factory.LastCode(email)!;
        Assert.False(string.IsNullOrWhiteSpace(code));
        var confirmed = await client.Client.PostAsJsonAsync("/api/account/delete/confirm", new { code });
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        var repeated = await client.Client.PostAsJsonAsync("/api/account/delete/confirm", new { code });
        Assert.NotEqual(HttpStatusCode.NoContent, repeated.StatusCode);
    }

    [Fact]
    public async Task Email_change_rejects_invalid_and_expired_codes()
    {
        var email = Unique();
        await factory.SeedUserAsync(email, Password);
        var client = await Login(email);
        var newEmail = Unique();
        Assert.Equal(HttpStatusCode.OK, (await client.Client.PostAsJsonAsync(
            "/api/account/email-change/send-code", new { newEmail })).StatusCode);

        var invalid = await client.Client.PostAsJsonAsync("/api/account/email-change/verify",
            new { newEmail, code = "999999" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.VerificationCodes.Where(c => c.Target == newEmail)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        var expired = await client.Client.PostAsJsonAsync("/api/account/email-change/verify",
            new { newEmail, code = factory.LastCode(newEmail) });
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
    }

    [Fact]
    public async Task Account_deletion_requires_second_factor_when_two_factor_is_enabled()
    {
        var email = Unique();
        await factory.SeedUserAsync(email, Password, twoFactor: true);
        var client = await Login(email);
        Assert.Equal(HttpStatusCode.OK,
            (await client.Client.PostAsync("/api/account/delete/send-code", null)).StatusCode);
        var code = factory.LastCode(email)!;

        var withoutSecondFactor = await client.Client.PostAsJsonAsync(
            "/api/account/delete/confirm", new { code });
        Assert.Equal(HttpStatusCode.BadRequest, withoutSecondFactor.StatusCode);

        var totp = await factory.TotpCodeAsync(email);
        var withSecondFactor = await client.Client.PostAsJsonAsync(
            "/api/account/delete/confirm", new { code, twoFactorCode = totp });
        Assert.Equal(HttpStatusCode.NoContent, withSecondFactor.StatusCode);
    }

    private async Task<(HttpClient Client, JsonDocument Login)> Login(string email)
    {
        var client = factory.CreateClient();
        var response = await factory.LoginAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", json.RootElement.GetProperty("accessToken").GetString());
        return (client, json);
    }

    private static string Unique() => $"account-{Guid.NewGuid():N}@test.io";
}
