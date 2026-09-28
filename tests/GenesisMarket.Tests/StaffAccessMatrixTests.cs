using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenesisMarket.Api.Auth;
using GenesisMarket.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Матрица доступа служебной зоны. Эндпоинты не перечисляются руками: они берутся из
/// таблицы маршрутов приложения (всё под /api/moderation и /api/admin плюс служебные
/// ручки вне этих префиксов), поэтому новый эндпоинт попадает в проверку сам.
/// Для каждого: аноним → 401; каждая роль ниже требуемой → 403; требуемая и выше →
/// доступ есть (не 401/403); сотрудник без 2FA → 403 <c>mfa_setup_required</c>.
/// </summary>
public class StaffAccessMatrixTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    // Служебные ручки вне префиксов /api/moderation и /api/admin.
    private static readonly string[] ExtraStaffRoutes = ["api/reviews/{id:guid}/hide"];

    private static readonly UserRole[] Roles = [UserRole.User, UserRole.Moderator, UserRole.Admin, UserRole.Owner];

    [Fact]
    public void Every_staff_endpoint_is_guarded_by_a_permission_policy()
    {
        var unguarded = StaffEndpoints()
            .Where(e => e.Permissions.Count == 0)
            .Select(e => $"{e.Method} {e.Route}")
            .ToList();

        Assert.True(unguarded.Count == 0,
            "Служебные эндпоинты без политики-разрешения:\n" + string.Join("\n", unguarded));
    }

    [Fact]
    public async Task Each_role_gets_403_above_its_level_and_access_at_or_above()
    {
        // Сотни запросов с одного адреса: глобальный лимит на IP здесь мешал бы проверке.
        var app = WithoutGlobalRateLimit();
        var clients = new Dictionary<UserRole, HttpClient>();
        foreach (var role in Roles)
            clients[role] = await ClientFor(app, role, twoFactor: true);

        var failures = new List<string>();
        foreach (var endpoint in StaffEndpoints())
        {
            var required = endpoint.MinimumRole;
            Assert.NotNull(required); // гарантирует тест выше

            foreach (var role in Roles)
            {
                var status = (await Send(clients[role], endpoint)).StatusCode;
                var expectedDenied = role < required;

                if (expectedDenied && status != HttpStatusCode.Forbidden)
                    failures.Add($"{role} → {endpoint.Method} {endpoint.Route}: ждали 403, получили {(int)status}");
                if (!expectedDenied && status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    failures.Add($"{role} → {endpoint.Method} {endpoint.Route}: ждали доступ, получили {(int)status}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public async Task Anonymous_gets_401_on_every_staff_endpoint()
    {
        var client = WithoutGlobalRateLimit().CreateClient();
        var failures = new List<string>();

        foreach (var endpoint in StaffEndpoints())
        {
            var status = (await Send(client, endpoint)).StatusCode;
            if (status != HttpStatusCode.Unauthorized)
                failures.Add($"{endpoint.Method} {endpoint.Route}: {(int)status}");
        }

        Assert.True(failures.Count == 0, "Аноним прошёл дальше 401:\n" + string.Join("\n", failures));
    }

    [Theory]
    [InlineData(UserRole.Moderator)]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Owner)]
    public async Task Staff_without_2fa_gets_403_mfa_setup_required_everywhere(UserRole role)
    {
        var app = WithoutGlobalRateLimit();
        var client = await ClientFor(app, role, twoFactor: false);

        var failures = new List<string>();
        foreach (var endpoint in StaffEndpoints())
        {
            var resp = await Send(client, endpoint);
            if (resp.StatusCode != HttpStatusCode.Forbidden)
            {
                failures.Add($"{endpoint.Method} {endpoint.Route}: {(int)resp.StatusCode}");
                continue;
            }

            // Ручка выше уровня роли — обычный 403: права нет вовсе, 2FA тут ни при чём.
            if (role < endpoint.MinimumRole)
                continue;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("code", out var code) || code.GetString() != "mfa_setup_required")
                failures.Add($"{endpoint.Method} {endpoint.Route}: 403 без кода mfa_setup_required");
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    // ---- helpers ----

    private sealed record StaffEndpoint(string Method, string Route, IReadOnlyList<string> Permissions, bool Multipart = false)
    {
        /// <summary>Младшая роль, у которой есть все разрешения эндпоинта.</summary>
        public UserRole? MinimumRole => Permissions.Count == 0
            ? null
            : Roles.First(r => Permissions.All(p => RolePermissions.Has(r, p)));
    }

    private IReadOnlyList<StaffEndpoint> StaffEndpoints() =>
        factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => (Endpoint: e, Route: e.RoutePattern.RawText?.TrimStart('/') ?? ""))
            .Where(x => x.Route.StartsWith("api/moderation", StringComparison.Ordinal) ||
                        x.Route.StartsWith("api/admin", StringComparison.Ordinal) ||
                        ExtraStaffRoutes.Contains(x.Route))
            .SelectMany(x => (x.Endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(method => new StaffEndpoint(
                    method,
                    x.Route,
                    x.Endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                        .Select(a => a.Policy)
                        .Where(p => p is not null && GenesisMarket.Api.Auth.Permissions.All.Contains(p))
                        .Select(p => p!)
                        .Distinct()
                        .ToList(),
                    // Ручка загрузки файла принимает только multipart: JSON-тело отсёк бы
                    // маршрутизатор (415) раньше авторизации, и проверка доступа не случилась бы.
                    x.Endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.ContentTypes
                        .Contains("multipart/form-data") == true)))
            .OrderBy(e => e.Route).ThenBy(e => e.Method)
            .ToList();

    /// <summary>Параметры маршрута — заведомо несуществующие объекты: проверяется доступ, не действие.</summary>
    private static string Url(string route) =>
        "/" + Regex.Replace(route, @"\{(\w+)(:(\w+))?\}", m => m.Groups[3].Value switch
        {
            "guid" => Guid.NewGuid().ToString(),
            "int" => "999999",
            _ => m.Groups[1].Value == "code" ? "10000" : "x"
        });

    private static Task<HttpResponseMessage> Send(HttpClient client, StaffEndpoint endpoint)
    {
        var request = new HttpRequestMessage(new HttpMethod(endpoint.Method), Url(endpoint.Route));
        if (endpoint.Multipart)
            request.Content = new MultipartFormDataContent { { new StringContent("x"), "note" } };
        else if (endpoint.Method is "POST" or "PUT" or "PATCH")
            request.Content = JsonContent.Create(new { });
        return client.SendAsync(request);
    }

    private WebApplicationFactory<Program> WithoutGlobalRateLimit() =>
        factory.WithWebHostBuilder(b => b.UseSetting("RateLimit:GlobalPerMinute", "100000"));

    private async Task<HttpClient> ClientFor(WebApplicationFactory<Program> app, UserRole role, bool twoFactor)
    {
        var email = $"matrix-{role}-{Guid.NewGuid():N}@test.io".ToLowerInvariant();
        await factory.SeedUserAsync(email, Password, role: role, twoFactor: twoFactor);

        var client = app.CreateClient();
        var login = await factory.LoginAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new("Bearer", doc.RootElement.GetProperty("accessToken").GetString());
        return client;
    }
}
