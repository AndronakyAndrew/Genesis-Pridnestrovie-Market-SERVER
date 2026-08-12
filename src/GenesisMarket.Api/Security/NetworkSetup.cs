using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace GenesisMarket.Api.Security;

/// <summary>
/// Доверие к заголовкам обратного прокси (Caddy как TLS-терминатор). Без него
/// <c>RemoteIpAddress</c> — это адрес прокси, и rate-limit/журнал считали бы всех
/// клиентов одним IP, а HSTS никогда бы не выставлялся. Доверенные прокси/сети —
/// строго из конфигурации (секция <c>Network</c>), чтобы XFF нельзя было подделать.
/// </summary>
public static class NetworkSetup
{
    public static IServiceCollection AddGenesisForwardedHeaders(
        this IServiceCollection services, IConfiguration configuration)
    {
        // KnownProxies — конкретные IP прокси; KnownNetworks — CIDR доверенных сетей.
        var proxies = Split(configuration["Network:KnownProxies"]);
        var networks = Split(configuration["Network:KnownNetworks"]);
        var forwardLimit = int.TryParse(configuration["Network:ForwardLimit"], out var fl) ? fl : 1;

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = forwardLimit;

            // Доверяем только явно перечисленным прокси/сетям (по умолчанию — только loopback).
            if (proxies.Length > 0 || networks.Length > 0)
            {
                options.KnownProxies.Clear();
                options.KnownIPNetworks.Clear();

                foreach (var p in proxies)
                    if (IPAddress.TryParse(p, out var ip))
                        options.KnownProxies.Add(ip);

                foreach (var n in networks)
                    if (System.Net.IPNetwork.TryParse(n, out var net))
                        options.KnownIPNetworks.Add(net);
            }
        });

        return services;
    }

    private static string[] Split(string? csv) =>
        (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
