using Microsoft.Extensions.Caching.Memory;

namespace GenesisMarket.Api.Auth;

public readonly record struct RateLimitResult(bool Allowed, TimeSpan RetryAfter);

/// <summary>
/// Ограничение частоты входа: login — 5 попыток / 15 мин на пару (IP, email).
/// Именно (IP, email) — встроенный RateLimiter на уровне middleware не видит email
/// (тело запроса ещё не прочитано), поэтому этот лимит остаётся проверкой в экшене.
/// Остальные лимиты (register, contact, reports, поиск, глобально) — на встроенном
/// RateLimiter (см. <c>RateLimitingSetup</c>).
/// </summary>
public interface IAuthRateLimiter
{
    RateLimitResult CheckLogin(string ip, string email);
    
    /// <summary>Запрос кода входа: 10 писем в час на IP (кулдаун на адрес — в сервисе).</summary>
    RateLimitResult CheckLoginCodeRequest(string ip);
}

public sealed class MemoryAuthRateLimiter(IMemoryCache cache) : IAuthRateLimiter
{
    private sealed class Counter(DateTimeOffset resetAt)
    {
        public int Count;
        public DateTimeOffset ResetAt { get; } = resetAt;
    }

    public RateLimitResult CheckLoginCodeRequest(string ip) =>
        Check($"rl:login-code:{ip}", limit: 10, window: TimeSpan.FromHours(1));

    public RateLimitResult CheckLogin(string ip, string email) =>
        Check($"rl:login:{ip}:{email.ToLowerInvariant()}", limit: 5, window: TimeSpan.FromMinutes(15));

    private RateLimitResult Check(string key, int limit, TimeSpan window)
    {
        var counter = cache.GetOrCreate(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = window;
            return new Counter(DateTimeOffset.UtcNow + window);
        })!;

        lock (counter)
        {
            counter.Count++;
            if (counter.Count <= limit)
                return new RateLimitResult(true, TimeSpan.Zero);

            var retry = counter.ResetAt - DateTimeOffset.UtcNow;
            return new RateLimitResult(false, retry > TimeSpan.Zero ? retry : TimeSpan.Zero);
        }
    }
}
