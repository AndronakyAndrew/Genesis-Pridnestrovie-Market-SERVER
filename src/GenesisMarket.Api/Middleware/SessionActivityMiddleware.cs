using GenesisMarket.Api.Auth;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GenesisMarket.Api.Middleware;

/// <summary>
/// Отметка «последняя активность» у сессии текущего запроса.
///
/// Пишем НЕ чаще раза в <see cref="Throttle"/> на сессию: иначе каждый запрос
/// к API превращался бы в UPDATE, то есть в запись на диск и распухание таблицы
/// — при 15-минутном access-токене это десятки бесполезных записей в минуту на
/// активного пользователя. Отсечка держится в памяти процесса (IMemoryCache):
/// потеря отсечки при рестарте безвредна — будет одна лишняя запись.
///
/// Значение заведомо грубое: «активность в пределах пяти минут» — это всё,
/// что нужно списку устройств, и заодно меньше похоже на журнал действий.
/// </summary>
public sealed class SessionActivityMiddleware(RequestDelegate next, IMemoryCache cache)
{
    /// <summary>Минимальный интервал между записями по одной сессии.</summary>
    public static readonly TimeSpan Throttle = TimeSpan.FromMinutes(5);

    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);

        // Отметку ставим только для аутентифицированных запросов со свежим claim'ом sid.
        var currentUser = context.RequestServices.GetRequiredService<ICurrentUser>();
        if (!currentUser.IsAuthenticated || currentUser.SessionId is not { } sessionId)
            return;

        if (currentUser.UserId is not { } userId)
            return;

        var key = $"session-seen:{sessionId}";
        if (cache.TryGetValue(key, out _))
            return;

        // Ставим отсечку ДО записи: параллельные запросы той же сессии не должны
        // устроить сразу несколько UPDATE.
        cache.Set(key, true, Throttle);

        try
        {
            var db = context.RequestServices.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;

            // Только активная строка своей сессии. UserId в условии — чтобы
            // подделанный sid из чужого токена ничего не задел.
            await db.RefreshTokens
                .Where(t => t.SessionId == sessionId && t.UserId == userId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastSeenAt, now), context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Отметка активности — не то, ради чего стоит ронять ответ:
            // он уже сформирован и, скорее всего, отправлен.
            context.RequestServices
                .GetRequiredService<ILogger<SessionActivityMiddleware>>()
                .LogWarning(ex, "Не удалось обновить LastSeenAt сессии {SessionId}", sessionId);
        }
    }
}
