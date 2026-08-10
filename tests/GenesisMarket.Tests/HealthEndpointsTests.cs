using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Дымовой интеграционный тест: приложение стартует и /health/live отвечает,
/// не требуя внешних зависимостей (liveness их не проверяет).
/// </summary>
public class HealthEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Liveness_returns_ok()
    {
        // Development: заполнены заглушки Minio/Postgres, клиент MinIO собирается.
        var client = factory
            .WithWebHostBuilder(b => b.UseEnvironment("Development"))
            .CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
