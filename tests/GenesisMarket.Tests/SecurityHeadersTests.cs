using System.Net;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Заголовки безопасности присутствуют на ответах API (в т.ч. на публичных GET).
/// HSTS не выставляется без TLS — тестовый сервер работает по http.
/// </summary>
public class SecurityHeadersTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Api_response_carries_security_headers()
    {
        var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/listings");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Equal("nosniff", Header(resp, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(resp, "X-Frame-Options"));
        Assert.Equal("no-referrer", Header(resp, "Referrer-Policy"));
        Assert.Contains("default-src 'none'", Header(resp, "Content-Security-Policy"));

        // Без TLS HSTS не выставляется.
        Assert.False(resp.Headers.Contains("Strict-Transport-Security"));
    }

    private static string Header(HttpResponseMessage resp, string name)
    {
        Assert.True(resp.Headers.TryGetValues(name, out var values), $"нет заголовка {name}");
        return string.Join(",", values!);
    }
}
