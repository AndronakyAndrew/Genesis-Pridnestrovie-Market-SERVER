using GenesisMarket.Api.Auth;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Trust;

/// <summary>
/// Rate-limit приёма жалоб: авторизованный лимитируется по UserId, аноним — по
/// IpHash. In-memory (на инстанс), тот же приём, что у раскрытия контактов.
/// </summary>
public interface IReportRateLimiter
{
    RateLimitResult Check(Guid? userId, string? ipHash);
}

public sealed class ReportRateLimiter(
    IMemoryCache cache,
    IOptions<TrustOptions> options) : IReportRateLimiter
{
    private readonly TrustOptions _o = options.Value;

    // Партиция rate-limit анонима, когда ключ хеширования IP не задан (dev).
    private const string NoKeyHash = "no-key";

    private sealed class Counter(DateTimeOffset resetAt)
    {
        public int Count;
        public DateTimeOffset ResetAt { get; } = resetAt;
    }

    public RateLimitResult Check(Guid? userId, string? ipHash)
    {
        var (key, limit) = userId is { } uid
            ? ($"report:u:{uid}", _o.UserReportsPerHour)
            : ($"report:ip:{ipHash ?? NoKeyHash}", _o.IpReportsPerHour);

        var window = TimeSpan.FromHours(1);
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
