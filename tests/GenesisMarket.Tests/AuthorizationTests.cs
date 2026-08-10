using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Domain.Enums;
using Xunit;

namespace GenesisMarket.Tests;

public class AuthorizationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    [Fact]
    public async Task Guest_can_browse_catalog_and_register()
    {
        var client = factory.CreateClient();

        // Каталог публичен для анонима.
        var catalog = await client.GetAsync("/api/listings");
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);

        // Регистрация доступна анониму.
        var register = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = Unique("guest"),
            password = Password,
            displayName = "Гость",
            city = "tiraspol",
            phone = "+37377100200"
        });
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
    }

    [Fact]
    public async Task Health_live_is_public()
    {
        var client = factory.CreateClient();
        var resp = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoints_reject_anonymous_with_401()
    {
        var client = factory.CreateClient();

        var sendCode = await client.PostAsync("/api/me/email/send-code", null);
        Assert.Equal(HttpStatusCode.Unauthorized, sendCode.StatusCode);

        var delete = await client.DeleteAsync($"/api/listings/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
    }

    [Fact]
    public async Task Listing_delete_anonymous_gets_401()
    {
        var ownerId = await factory.SeedUserAsync(Unique("owner"), Password);
        var listingId = await factory.SeedListingAsync(ownerId);

        var client = factory.CreateClient();
        var resp = await client.DeleteAsync($"/api/listings/{listingId}");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Listing_delete_by_other_user_is_forbidden()
    {
        var ownerId = await factory.SeedUserAsync(Unique("owner"), Password);
        var listingId = await factory.SeedListingAsync(ownerId);

        var otherEmail = Unique("other");
        await factory.SeedUserAsync(otherEmail, Password);
        var client = await AuthedClient(otherEmail);

        var resp = await client.DeleteAsync($"/api/listings/{listingId}");

        // Существование объявления публично (каталог) → 403, не 404.
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Listing_delete_by_owner_succeeds()
    {
        var ownerEmail = Unique("owner");
        var ownerId = await factory.SeedUserAsync(ownerEmail, Password);
        var listingId = await factory.SeedListingAsync(ownerId);
        var client = await AuthedClient(ownerEmail);

        var resp = await client.DeleteAsync($"/api/listings/{listingId}");

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task Listing_delete_by_moderator_succeeds()
    {
        var ownerId = await factory.SeedUserAsync(Unique("owner"), Password);
        var listingId = await factory.SeedListingAsync(ownerId);

        var modEmail = Unique("mod");
        await factory.SeedUserAsync(modEmail, Password, role: UserRole.Moderator);
        var client = await AuthedClient(modEmail);

        var resp = await client.DeleteAsync($"/api/listings/{listingId}");

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
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
