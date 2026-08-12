namespace GenesisMarket.Api.Security;

/// <summary>
/// Заголовки безопасности на каждом ответе (в т.ч. на ошибках — стоит рано в пайплайне).
/// HSTS выставляется только за TLS (после ForwardedHeaders <c>Request.IsHttps</c> корректен).
/// CSP строгий для API-ответов; ослабленный — только для Swagger UI (нужны inline-скрипты/стили).
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    // Строгий CSP для JSON-API: ресурсы не грузятся, кадрирование запрещено.
    private const string ApiCsp = "default-src 'none'; frame-ancestors 'none'";

    // Swagger UI рендерит инлайновые скрипты/стили и data:-изображения.
    private const string SwaggerCsp =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";

        var isSwagger = context.Request.Path.StartsWithSegments("/swagger");
        headers["Content-Security-Policy"] = isSwagger ? SwaggerCsp : ApiCsp;

        // Только за TLS-терминатором: два года, включая поддомены.
        if (context.Request.IsHttps)
            headers["Strict-Transport-Security"] = "max-age=63072000; includeSubDomains";

        return next(context);
    }
}
