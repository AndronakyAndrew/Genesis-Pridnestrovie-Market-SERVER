using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Восстановление пароля по ссылке из письма: анти-перечисление на запросе,
/// одноразовость ссылки и обрыв всех сессий после смены пароля.
/// </summary>
public class PasswordResetTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string GoodPassword = "CorrectHorse7";
    private const string NewPassword = "FreshBattery42";

    [Fact]
    public async Task Forgot_password_answers_the_same_for_known_and_unknown_email()
    {
        var known = Unique("known");
        await factory.SeedUserAsync(known, GoodPassword);
        var unknown = Unique("nobody");
        var client = factory.CreateClient();

        var forKnown = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = known });
        var forUnknown = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = unknown });

        Assert.Equal(HttpStatusCode.OK, forKnown.StatusCode);
        Assert.Equal(HttpStatusCode.OK, forUnknown.StatusCode);
        Assert.Equal(await BodyOf(forKnown), await BodyOf(forUnknown));

        // Письмо ушло только существующему адресу.
        Assert.NotNull(factory.LastEmail(known));
        Assert.Null(factory.LastEmail(unknown));
    }

    [Fact]
    public async Task Reset_link_changes_password_and_ends_all_sessions()
    {
        var email = Unique("reset");
        await factory.SeedUserAsync(email, GoodPassword);
        var client = factory.CreateClient();

        // Живая сессия до восстановления — её должно оборвать.
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = GoodPassword });
        var refreshToken = await FieldOf(login, "refreshToken");

        var token = await RequestResetTokenAsync(client, email);

        var reset = await client.PostAsJsonAsync("/api/auth/reset-password",
            new { token, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        // Старый пароль больше не работает, новый — работает.
        var withOld = await client.PostAsJsonAsync("/api/auth/login", new { email, password = GoodPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, withOld.StatusCode);

        var withNew = await client.PostAsJsonAsync("/api/auth/login", new { email, password = NewPassword });
        Assert.Equal(HttpStatusCode.OK, withNew.StatusCode);

        // Refresh-токен прежней сессии отозван.
        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Reset_link_works_only_once()
    {
        var email = Unique("once");
        await factory.SeedUserAsync(email, GoodPassword);
        var client = factory.CreateClient();

        var token = await RequestResetTokenAsync(client, email);

        var first = await client.PostAsJsonAsync("/api/auth/reset-password",
            new { token, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/auth/reset-password",
            new { token, newPassword = "AnotherPass88" });
        Assert.Equal(HttpStatusCode.Gone, second.StatusCode);
    }

    [Fact]
    public async Task Reset_with_unknown_token_is_rejected()
    {
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/auth/reset-password",
            new { token = "definitely-not-a-real-token", newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Reset_rejects_password_that_fails_policy()
    {
        var email = Unique("weak");
        await factory.SeedUserAsync(email, GoodPassword);
        var client = factory.CreateClient();

        var token = await RequestResetTokenAsync(client, email);

        var resp = await client.PostAsJsonAsync("/api/auth/reset-password",
            new { token, newPassword = "123" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        // Отклонённая попытка не сжигает ссылку — пароль всё ещё можно сменить.
        var retry = await client.PostAsJsonAsync("/api/auth/reset-password",
            new { token, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
    }

    // ---- helpers ----

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";

    /// <summary>Запрашивает письмо и достаёт из него токен ссылки (как это сделал бы человек).</summary>
    private async Task<string> RequestResetTokenAsync(HttpClient client, string email)
    {
        var resp = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var mail = factory.LastEmail(email.ToLowerInvariant());
        Assert.NotNull(mail);

        var match = Regex.Match(mail!.Text, @"/reset-password\?token=([^\s]+)");
        Assert.True(match.Success, "В письме нет ссылки восстановления");
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }

    private static Task<string> BodyOf(HttpResponseMessage resp) => resp.Content.ReadAsStringAsync();

    private static async Task<string> FieldOf(HttpResponseMessage resp, string field)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty(field).GetString()!;
    }
}
