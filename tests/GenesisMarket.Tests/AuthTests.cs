using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GenesisMarket.Tests;

public class AuthTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string GoodPassword = "CorrectHorse7";

    [Fact]
    public async Task Login_unknown_email_and_wrong_password_return_identical_response()
    {
        var email = Unique("ident");
        await factory.SeedUserAsync(email, GoodPassword);
        var client = factory.CreateClient();

        var unknown = await client.PostAsJsonAsync("/api/auth/login",
            new { email = Unique("nobody"), password = "SomePass99" });
        var wrongPwd = await client.PostAsJsonAsync("/api/auth/login",
            new { email, password = "WrongPass99" });

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPwd.StatusCode);

        // Одинаковый код и текст (traceId в ProblemDetails может отличаться — это ок).
        Assert.Equal(await TitleOf(unknown), await TitleOf(wrongPwd));
        Assert.Equal("Неверный email или пароль", await TitleOf(unknown));
    }

    [Fact]
    public async Task Banned_user_access_token_stops_working()
    {
        var email = Unique("ban");
        var userId = await factory.SeedUserAsync(email, GoodPassword);
        var client = factory.CreateClient();

        var access = await LoginAndGetAccessToken(client, email, GoodPassword);
        client.DefaultRequestHeaders.Authorization = new("Bearer", access);

        // До бана токен работает (эндпоинт защищён [Authorize]).
        var before = await client.PostAsync("/api/me/phone/send-code", null);
        Assert.NotEqual(HttpStatusCode.Unauthorized, before.StatusCode);

        await factory.BanUserAsync(userId);

        // После бана — 401 (SecurityStampValidator блокирует на уровне валидации токена).
        var after = await client.PostAsync("/api/me/phone/send-code", null);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Reusing_rotated_refresh_token_revokes_the_chain()
    {
        var email = Unique("rotate");
        await factory.SeedUserAsync(email, GoodPassword);
        var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = GoodPassword });
        var refresh1 = await FieldOf(login, "refreshToken");

        // Ротация: refresh1 → refresh2, refresh1 становится отозванным.
        var rotate = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh1 });
        Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
        var refresh2 = await FieldOf(rotate, "refreshToken");

        // Повторное использование refresh1 = кража → 401 и отзыв всей цепочки.
        var reuse = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh1 });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);

        // refresh2 из той же цепочки тоже больше не работает.
        var afterReuse = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh2 });
        Assert.Equal(HttpStatusCode.Unauthorized, afterReuse.StatusCode);
    }

    [Fact]
    public async Task Password_of_73_bytes_is_rejected_not_truncated()
    {
        var client = factory.CreateClient();
        var password = new string('a', 73); // 73 ASCII-байта > 72

        var resp = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = Unique("long"),
            password,
            displayName = "Пользователь",
            city = "tiraspol",
            phone = "+37377123456"
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Login_response_has_no_password_hash_security_stamp_or_role()
    {
        var email = Unique("nofields");
        await factory.SeedUserAsync(email, GoodPassword);
        var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = GoodPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var body = await login.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordhash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("securitystamp", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"role\"", body, StringComparison.OrdinalIgnoreCase);
    }

    // ---- helpers ----

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";

    private static async Task<string> LoginAndGetAccessToken(HttpClient client, string email, string password)
    {
        var resp = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await FieldOf(resp, "accessToken");
    }

    private static async Task<string> FieldOf(HttpResponseMessage resp, string field)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty(field).GetString()!;
    }

    private static async Task<string?> TitleOf(HttpResponseMessage resp)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
    }
}
