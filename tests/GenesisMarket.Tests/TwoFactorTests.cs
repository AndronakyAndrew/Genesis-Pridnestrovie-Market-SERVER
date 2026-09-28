using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GenesisMarket.Api.Auth.TwoFactor;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// TOTP 2FA: алгоритм (векторы RFC 6238), настройка и включение, вход в два шага,
/// защита от повтора кода, резервные коды, лимит попыток на билет, обязательность
/// для сотрудников и короткая служебная сессия.
/// </summary>
public class TwoFactorTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    // ---- Алгоритм ----

    [Theory]
    // RFC 6238, приложение B, SHA1, секрет «12345678901234567890»; 8-значные коды
    // усечены до 6 младших цифр — ровно так их считают приложения-аутентификаторы.
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Totp_matches_rfc6238_vectors(long unixTime, string expected)
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal(expected, Totp.Compute(secret, unixTime / Totp.StepSeconds));
    }

    [Fact]
    public void Totp_accepts_adjacent_step_and_rejects_replay()
    {
        var secret = Totp.NewSecret();
        var now = DateTimeOffset.UtcNow;
        var step = Totp.TimeStep(now);

        Assert.Equal(step - 1, Totp.Verify(secret, Totp.Compute(secret, step - 1), now, lastUsedStep: 0));
        Assert.Null(Totp.Verify(secret, Totp.Compute(secret, step - 2), now, lastUsedStep: 0));
        // Шаг уже использован — тот же код второй раз не проходит.
        Assert.Null(Totp.Verify(secret, Totp.Compute(secret, step), now, lastUsedStep: step));
        Assert.Null(Totp.Verify(secret, "12345", now, 0));
        Assert.Null(Totp.Verify(secret, "abcdef", now, 0));
    }

    [Fact]
    public void Base32_matches_rfc4648()
    {
        Assert.Equal("MZXW6YTBOI", Base32.Encode("foobar"u8));
        Assert.Equal("MY", Base32.Encode("f"u8));
    }

    // ---- Настройка и вход ----

    [Fact]
    public async Task Setup_enable_then_login_requires_second_factor()
    {
        var email = Unique("enable");
        await factory.SeedUserAsync(email, Password);
        var client = await Login(email);

        using var setup = await Json(await client.PostAsync("/api/me/2fa/setup", null), HttpStatusCode.OK);
        var secretB32 = setup.RootElement.GetProperty("secret").GetString()!;
        var uri = setup.RootElement.GetProperty("provisioningUri").GetString()!;
        Assert.StartsWith("otpauth://totp/", uri);
        // Название сервиса в приложении-аутентификаторе — фирменное, не старое «Genesis Market».
        Assert.Contains("issuer=" + Uri.EscapeDataString("Местная площадка Genesis"), uri);
        Assert.DoesNotContain("Genesis%20Market", uri);
        var secret = FromBase32(secretB32);

        // Неверный код — не включает.
        var wrong = await client.PostAsJsonAsync("/api/me/2fa/enable", new { code = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        using var enabled = await Json(await client.PostAsJsonAsync("/api/me/2fa/enable",
            new { code = Totp.Compute(secret, Totp.TimeStep(DateTimeOffset.UtcNow)) }), HttpStatusCode.OK);
        var codes = enabled.RootElement.GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Equal(10, codes.Count);
        Assert.Equal(10, codes.Distinct().Count());

        // Теперь пароль даёт только билет на второй шаг.
        var anon = factory.CreateClient();
        var first = await anon.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        using var challenge = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.False(challenge.RootElement.TryGetProperty("accessToken", out _));
        var ticket = challenge.RootElement.GetProperty("challengeToken").GetString();

        var ok = await anon.PostAsJsonAsync("/api/auth/2fa/verify",
            new { challengeToken = ticket, code = await factory.TotpCodeAsync(email, secret) });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // Билет одноразовый.
        var again = await anon.PostAsJsonAsync("/api/auth/2fa/verify",
            new { challengeToken = ticket, code = await factory.TotpCodeAsync(email, secret) });
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
    }

    [Fact]
    public async Task Same_totp_code_is_not_accepted_twice()
    {
        var email = Unique("replay");
        await factory.SeedUserAsync(email, Password, twoFactor: true);
        var code = await factory.TotpCodeAsync(email);

        Assert.Equal(HttpStatusCode.OK, (await VerifyWith(email, code)).StatusCode);
        // Тот же код в том же окне — отказ (сбрасывать LastUsedTimeStep здесь нельзя).
        Assert.Equal(HttpStatusCode.Unauthorized, (await VerifyWith(email, code)).StatusCode);
    }

    [Fact]
    public async Task Recovery_code_works_once_and_is_journaled()
    {
        var email = Unique("recovery");
        await factory.SeedUserAsync(email, Password);
        var client = await Login(email);
        var codes = await EnableTwoFactor(client, email);

        Assert.Equal(HttpStatusCode.OK, (await VerifyWith(email, codes[0].ToLowerInvariant())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await VerifyWith(email, codes[0])).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).FirstAsync();
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "2fa.recovery_used" && e.TargetId == userId.ToString()));
    }

    [Fact]
    public async Task Challenge_dies_after_max_attempts_even_with_correct_code()
    {
        var email = Unique("bruteforce");
        await factory.SeedUserAsync(email, Password, twoFactor: true);

        var anon = factory.CreateClient();
        var first = await anon.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        using var challenge = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var ticket = challenge.RootElement.GetProperty("challengeToken").GetString();

        for (var i = 0; i < 5; i++)
            await anon.PostAsJsonAsync("/api/auth/2fa/verify", new { challengeToken = ticket, code = "000000" });

        var correct = await anon.PostAsJsonAsync("/api/auth/2fa/verify",
            new { challengeToken = ticket, code = await factory.TotpCodeAsync(email) });
        Assert.Equal(HttpStatusCode.Unauthorized, correct.StatusCode);
    }

    [Fact]
    public async Task Enabling_2fa_ends_other_sessions_and_opens_staff_area_in_current_one()
    {
        var email = Unique("modsetup");
        await factory.SeedUserAsync(email, Password, role: UserRole.Moderator, twoFactor: false);
        var other = await Login(email);
        var current = await Login(email);

        var denied = await current.GetAsync("/api/moderation/queue");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("mfa_setup_required", await denied.Content.ReadAsStringAsync());

        using var status = await Json(await current.GetAsync("/api/me/2fa"), HttpStatusCode.OK);
        Assert.True(status.RootElement.GetProperty("required").GetBoolean());
        Assert.False(status.RootElement.GetProperty("enabled").GetBoolean());

        using var setup = await Json(await current.PostAsync("/api/me/2fa/setup", null), HttpStatusCode.OK);
        var secret = FromBase32(setup.RootElement.GetProperty("secret").GetString()!);
        using var enabled = await Json(await current.PostAsJsonAsync("/api/me/2fa/enable",
            new { code = Totp.Compute(secret, Totp.TimeStep(DateTimeOffset.UtcNow)) }), HttpStatusCode.OK);

        // Новый access-токен с mfa открывает служебный раздел без повторного входа.
        current.DefaultRequestHeaders.Authorization =
            new("Bearer", enabled.RootElement.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await current.GetAsync("/api/moderation/queue")).StatusCode);

        // Сотрудник не может отключить 2FA.
        var disable = await current.PostAsJsonAsync("/api/me/2fa/disable",
            new { code = await factory.TotpCodeAsync(email, secret) });
        Assert.Equal(HttpStatusCode.Forbidden, disable.StatusCode);

        // Сессии без второго фактора завершены при включении (refresh отозван).
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).FirstAsync();
        Assert.Equal(1, await db.RefreshTokens.CountAsync(t => t.UserId == userId && t.RevokedAt == null));
        _ = other;
    }

    [Fact]
    public async Task Regular_user_can_disable_2fa_with_code()
    {
        var email = Unique("disable");
        await factory.SeedUserAsync(email, Password);
        var client = await Login(email);
        await EnableTwoFactor(client, email);

        var disable = await client.PostAsJsonAsync("/api/me/2fa/disable", new { code = await factory.TotpCodeAsync(email, _lastSecret) });
        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);

        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    // ---- Служебная сессия ----

    [Fact]
    public async Task Staff_refresh_token_is_short_and_dies_after_idle()
    {
        var email = Unique("staffsession");
        var userId = await factory.SeedUserAsync(email, Password, role: UserRole.Admin);

        var client = factory.CreateClient();
        using var login = await Json(await factory.LoginAsync(client, email, Password), HttpStatusCode.OK);
        var refreshExpires = login.RootElement.GetProperty("refreshTokenExpiresAt").GetDateTimeOffset();
        Assert.True(refreshExpires <= DateTimeOffset.UtcNow.AddHours(12).AddMinutes(1),
            $"Служебный refresh живёт до {refreshExpires:O} — дольше 12 часов.");

        // Обновление сохраняет второй фактор: служебный раздел открыт и после refresh.
        var refreshToken = login.RootElement.GetProperty("refreshToken").GetString();
        using var refreshed = await Json(await factory.CreateClient()
            .PostAsJsonAsync("/api/auth/refresh", new { refreshToken }), HttpStatusCode.OK);
        var after = factory.CreateClient();
        after.DefaultRequestHeaders.Authorization = new("Bearer", refreshed.RootElement.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await after.GetAsync("/api/moderation/queue")).StatusCode);

        // Простой дольше часа — refresh больше не работает.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.RefreshTokens.Where(t => t.UserId == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastSeenAt, DateTimeOffset.UtcNow.AddHours(-2)));
        }
        var stale = await factory.CreateClient().PostAsJsonAsync("/api/auth/refresh",
            new { refreshToken = refreshed.RootElement.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
    }

    [Fact]
    public async Task Regular_user_refresh_token_keeps_30_days()
    {
        var email = Unique("regular");
        await factory.SeedUserAsync(email, Password);
        using var login = await Json(await factory.LoginAsync(factory.CreateClient(), email, Password), HttpStatusCode.OK);
        Assert.True(login.RootElement.GetProperty("refreshTokenExpiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddDays(29));
    }

    // ---- helpers ----

    private byte[] _lastSecret = [];

    private async Task<List<string>> EnableTwoFactor(HttpClient client, string email)
    {
        using var setup = await Json(await client.PostAsync("/api/me/2fa/setup", null), HttpStatusCode.OK);
        _lastSecret = FromBase32(setup.RootElement.GetProperty("secret").GetString()!);
        using var enabled = await Json(await client.PostAsJsonAsync("/api/me/2fa/enable",
            new { code = Totp.Compute(_lastSecret, Totp.TimeStep(DateTimeOffset.UtcNow)) }), HttpStatusCode.OK);
        return enabled.RootElement.GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
    }

    private async Task<HttpResponseMessage> VerifyWith(string email, string code)
    {
        var anon = factory.CreateClient();
        var first = await anon.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        using var challenge = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        return await anon.PostAsJsonAsync("/api/auth/2fa/verify",
            new { challengeToken = challenge.RootElement.GetProperty("challengeToken").GetString(), code });
    }

    private async Task<HttpClient> Login(string email)
    {
        var client = factory.CreateClient();
        using var doc = await Json(await factory.LoginAsync(client, email, Password), HttpStatusCode.OK);
        client.DefaultRequestHeaders.Authorization = new("Bearer", doc.RootElement.GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task<JsonDocument> Json(HttpResponseMessage resp, HttpStatusCode expected)
    {
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == expected, $"{(int)resp.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    private static byte[] FromBase32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in value)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return bytes.ToArray();
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";
}
