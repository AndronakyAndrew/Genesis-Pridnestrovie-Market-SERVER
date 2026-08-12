using System.Text.Json;
using System.Threading.RateLimiting;
using GenesisMarket.Api.Listings;
using GenesisMarket.Api.Trust;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GenesisMarket.Api.Security;

/// <summary>Имена политик rate-limit (для атрибутов <c>[EnableRateLimiting]</c>).</summary>
public static class RateLimitPolicies
{
    /// <summary>Чувствительные анонимные POST-ы (register, forgot-password): 3/час на IP.</summary>
    public const string SensitiveAnon = "sensitive-anon";

    /// <summary>Раскрытие контактов: аноним — на IP, авторизованный — на пользователя.</summary>
    public const string Contact = "contact";

    /// <summary>Создание объявления: на пользователя.</summary>
    public const string CreateListing = "create-listing";

    /// <summary>Приём жалоб: аноним — на IP, авторизованный — на пользователя.</summary>
    public const string Report = "report";

    /// <summary>Поиск по каталогу: на IP.</summary>
    public const string Search = "search";
}

/// <summary>
/// Числовые лимиты, у которых нет «родного» дома в других секциях. Секция <c>RateLimit</c>.
/// Лимиты contact/report берутся из <see cref="ContactRevealOptions"/> и <see cref="TrustOptions"/>
/// (единый источник правды с остальной анти-скрейпинг логикой).
/// </summary>
public sealed class RateLimitOptions
{
    public const string Section = "RateLimit";

    /// <summary>Глобальный потолок на IP, запросов в минуту.</summary>
    public int GlobalPerMinute { get; set; } = 300;

    /// <summary>Поиск по каталогу, запросов в минуту на IP.</summary>
    public int SearchPerMinute { get; set; } = 60;

    /// <summary>Чувствительные анонимные POST-ы, запросов в час на IP.</summary>
    public int SensitiveAnonPerHour { get; set; } = 3;

    /// <summary>Создание объявлений, запросов в час на пользователя.</summary>
    public int CreateListingPerHour { get; set; } = 10;
}

public static class RateLimitingSetup
{
    /// <summary>
    /// Единая политика rate-limit на встроенном <c>RateLimiter</c>: глобальный лимит
    /// на IP + именованные политики по эндпоинтам. Ответ на превышение — 429 с
    /// <c>Retry-After</c> и телом ProblemDetails; факт пишется в журнал безопасности.
    /// login здесь НЕ лимитируется middleware — точный лимит (IP,email) живёт в экшене.
    /// </summary>
    public static IServiceCollection AddGenesisRateLimiting(
        this IServiceCollection services, IConfiguration configuration)
    {
        var rl = configuration.GetSection(RateLimitOptions.Section).Get<RateLimitOptions>() ?? new RateLimitOptions();
        var contact = configuration.GetSection(ContactRevealOptions.Section).Get<ContactRevealOptions>() ?? new ContactRevealOptions();
        var trust = configuration.GetSection(TrustOptions.Section).Get<TrustOptions>() ?? new TrustOptions();

        services.Configure<RateLimitOptions>(configuration.GetSection(RateLimitOptions.Section));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Глобальный лимит: на каждый IP — фиксированное окно в минуту.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                FixedByKey($"global:{Ip(ctx)}", rl.GlobalPerMinute, TimeSpan.FromMinutes(1)));

            // Поиск по каталогу — на IP.
            options.AddPolicy(RateLimitPolicies.Search, ctx =>
                FixedByKey($"search:{Ip(ctx)}", rl.SearchPerMinute, TimeSpan.FromMinutes(1)));

            // Чувствительные анонимные POST-ы — на IP.
            options.AddPolicy(RateLimitPolicies.SensitiveAnon, ctx =>
                FixedByKey($"sensitive:{Ip(ctx)}", rl.SensitiveAnonPerHour, TimeSpan.FromHours(1)));

            // Создание объявления — на пользователя (эндпоинт требует аутентификации).
            options.AddPolicy(RateLimitPolicies.CreateListing, ctx =>
                FixedByKey($"listing:u:{UserOrIp(ctx)}", rl.CreateListingPerHour, TimeSpan.FromHours(1)));

            // Раскрытие контактов: авторизованный — на пользователя (30), аноним — на IP (10).
            options.AddPolicy(RateLimitPolicies.Contact, ctx => UserId(ctx) is { } uid
                ? FixedByKey($"contact:u:{uid}", contact.UserPerHour, TimeSpan.FromHours(1))
                : FixedByKey($"contact:ip:{Ip(ctx)}", contact.AnonPerHour, TimeSpan.FromHours(1)));

            // Жалобы: авторизованный — на пользователя (20), аноним — на IP (5).
            options.AddPolicy(RateLimitPolicies.Report, ctx => UserId(ctx) is { } uid
                ? FixedByKey($"report:u:{uid}", trust.UserReportsPerHour, TimeSpan.FromHours(1))
                : FixedByKey($"report:ip:{Ip(ctx)}", trust.IpReportsPerHour, TimeSpan.FromHours(1)));

            options.OnRejected = async (context, ct) =>
            {
                var http = context.HttpContext;

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    http.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();

                // Журнал безопасности: срабатывание лимита.
                var policy = http.GetEndpoint()?.Metadata
                    .GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "global";
                http.RequestServices.GetService<ISecurityAudit>()?.RateLimited(policy, http.Request.Path);

                var problem = new ProblemDetails
                {
                    Title = "Слишком много запросов, попробуйте позже",
                    Status = StatusCodes.Status429TooManyRequests
                };
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await http.Response.WriteAsJsonAsync(
                    problem, (JsonSerializerOptions?)null, "application/problem+json", ct);
            };
        });

        return services;
    }

    private static RateLimitPartition<string> FixedByKey(string key, int permitLimit, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0
        });

    // IP клиента после ForwardedHeaders (за Caddy — реальный адрес). "unknown", если недоступен.
    private static string Ip(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static Guid? UserId(HttpContext ctx) =>
        (ctx.User.Identity?.IsAuthenticated ?? false)
        && Guid.TryParse(ctx.User.FindFirst("sub")?.Value, out var id) ? id : null;

    // Для эндпоинтов «на пользователя», где неаутентифицированный доступ невозможен;
    // страховка на случай сбоя — партиция по IP.
    private static string UserOrIp(HttpContext ctx) =>
        UserId(ctx)?.ToString() ?? Ip(ctx);
}
