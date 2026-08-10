using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GenesisMarket.Tests;

public class ProfileTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    [Fact]
    public async Task Patch_with_role_admin_in_body_does_not_change_role()
    {
        var email = Unique("patchrole");
        await factory.SeedUserAsync(email, Password); // роль User
        var client = await AuthedClient(email);

        // В тело кладём легитимное поле + запрещённые (role/email/isBanned).
        var patch = await client.PatchAsJsonAsync("/api/me", new
        {
            displayName = "Новое имя",
            role = "Admin",
            isBanned = false,
            email = "hacker@evil.io"
        });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal("User", me.GetProperty("role").GetString());          // роль не изменилась
        Assert.Equal("Новое имя", me.GetProperty("displayName").GetString()); // легитимное поле применилось
        Assert.Equal(email, me.GetProperty("email").GetString());          // email не изменился
    }

    [Fact]
    public async Task Public_profile_never_contains_email_phone_or_role()
    {
        var email = Unique("pub");
        var userId = await factory.SeedUserAsync(email, Password);
        var client = factory.CreateClient();

        var resp = await client.GetAsync($"/api/users/{userId}/public");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain(email, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"email\"", body, StringComparison.OrdinalIgnoreCase);
        // Номер телефона не раскрывается (флаг phoneVerified — можно, он в спеке).
        Assert.DoesNotContain("phoneE164", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("+373", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"role\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isbanned", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_me_excludes_password_hash_and_security_stamp()
    {
        var email = Unique("me");
        await factory.SeedUserAsync(email, Password);
        var client = await AuthedClient(email);

        var resp = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordhash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("securitystamp", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_me_requires_authentication()
    {
        var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_me_anonymizes_archives_listings_and_invalidates_token()
    {
        var email = Unique("del");
        var userId = await factory.SeedUserAsync(email, Password);
        var listingId = await factory.SeedListingAsync(userId);
        var client = await AuthedClient(email);

        var delete = await client.DeleteAsync("/api/me");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        // Токен удалённого пользователя перестаёт работать сразу.
        var afterMe = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, afterMe.StatusCode);

        // Публичный профиль удалённого — 404.
        var pub = await factory.CreateClient().GetAsync($"/api/users/{userId}/public");
        Assert.Equal(HttpStatusCode.NotFound, pub.StatusCode);

        // Анонимизация и архивация — проверяем в БД.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
        Assert.True(user.IsDeleted);
        Assert.Equal($"deleted-{userId}@invalid", user.Email);
        Assert.Null(user.PhoneE164);

        var status = await db.Listings.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.Id == listingId).Select(l => l.Status).FirstAsync();
        Assert.Equal(ListingStatus.Archived, status);
    }

    [Fact]
    public async Task Patch_changing_phone_resets_verification()
    {
        var email = Unique("phone");
        await factory.SeedUserAsync(email, Password, phoneVerified: true);
        var client = await AuthedClient(email);

        var patch = await client.PatchAsJsonAsync("/api/me", new { phoneE164 = "+37377654321" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal("+37377654321", me.GetProperty("phoneE164").GetString());
        Assert.False(me.GetProperty("phoneVerified").GetBoolean()); // сброшено сменой номера
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
