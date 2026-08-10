using Microsoft.Extensions.Caching.Memory;

namespace GenesisMarket.Api.Auth;

public readonly record struct RateLimitResult(bool Allowed, TimeSpan RetryAfter);

/// <summary>
/// Ограничение частоты для аутентификации:
/// login — 5 попыток / 15 мин на пару (IP, email);
/// register — 3 / час на IP.
/// </summary>
public interface IAuthRateLimiter
{
    RateLimitResult CheckLogin(string ip, string email);
    RateLimitResult CheckRegister(string ip);
}

public sealed class MemoryAuthRateLimiter(IMemoryCache cache) : IAuthRateLimiter
{
    private sealed class Counter(DateTimeOffset resetAt)
    {
        public int Count;
        public DateTimeOffset ResetAt { get; } = resetAt;
    }

    public RateLimitResult CheckLogin(string ip, string email) =>
        Check($"rl:login:{ip}:{email.ToLowerInvariant()}", limit: 5, window: TimeSpan.FromMinutes(15));

    public RateLimitResult CheckRegister(string ip) =>
        Check($"rl:register:{ip}", limit: 3, window: TimeSpan.FromHours(1));

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
