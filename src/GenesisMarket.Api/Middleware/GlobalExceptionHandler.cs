using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace GenesisMarket.Api.Middleware;

/// <summary>
/// Глобальный обработчик необработанных исключений.
/// Ответ — ProblemDetails по RFC 7807. В Production наружу не уходят
/// ни стектрейсы, ни тексты SQL, ни имена констрейнтов — только TraceId,
/// по которому детали находятся в логах.
/// </summary>
public sealed class GlobalExceptionHandler(
    IHostEnvironment environment,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        // Полные детали — только в лог, никогда в ответ.
        logger.LogError(
            exception,
            "Необработанное исключение. TraceId={TraceId} Path={Path}",
            traceId,
            httpContext.Request.Path);

        var problem = new ProblemDetails
        
        {
            Type = "https://httpstatuses.io/500",
            Title = "Внутренняя ошибка сервера",
            Status = StatusCodes.Status500InternalServerError,
            Instance = httpContext.Request.Path
        };

        // TraceId есть всегда — по нему клиент ссылается на запись в логах.
        problem.Extensions["traceId"] = traceId;

        if (environment.IsDevelopment())
        {
            // Диагностика — только вне Production.
            problem.Detail = exception.Message;
            problem.Extensions["exceptionType"] = exception.GetType().FullName;
            problem.Extensions["stackTrace"] = exception.StackTrace;
        }
        else
        {
            problem.Detail = "Произошла ошибка. Обратитесь в поддержку и укажите traceId.";
        }

        httpContext.Response.StatusCode = problem.Status.Value;
        await httpContext.Response
            .WriteAsJsonAsync(problem, cancellationToken);

        return true;
    }
}
