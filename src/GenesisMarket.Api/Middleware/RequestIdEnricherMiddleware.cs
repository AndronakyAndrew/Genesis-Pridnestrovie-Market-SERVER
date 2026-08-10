using Serilog.Context;

namespace GenesisMarket.Api.Middleware;

/// <summary>
/// Кладёт RequestId в LogContext, чтобы Serilog обогащал каждую запись
/// идентификатором запроса. Тот же идентификатор возвращается клиенту в заголовке.
/// </summary>
public sealed class RequestIdEnricherMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = context.TraceIdentifier;

        context.Response.Headers["X-Request-Id"] = requestId;

        using (LogContext.PushProperty("RequestId", requestId))
        {
            await next(context);
        }
    }
}
