using GenesisMarket.Api.Auth;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Listings;

/// <summary>
/// Анти-скрейпинг раскрытия контактов: хеширование IP, rate-limit с
/// партиционированием по (IpHash, UserId), намеренная задержка анонимам,
/// журналирование каждого раскрытия и алерт при аномальной активности.
/// </summary>
public interface IContactRevealService
{
    /// <summary>HMAC-SHA256 от IP (hex). Партиция rate-limit и значение для журнала.</summary>
    string HashIp(string? ip);

    /// <summary>Rate-limit по (IpHash, UserId): аноним — по IpHash, авторизованный — по UserId.</summary>
    RateLimitResult CheckRateLimit(string ipHash, Guid? userId);

    /// <summary>Задержка ответа анонимам (случайная), чтобы массовый обход был дороже.</summary>
    Task DelayAnonymousAsync(CancellationToken ct);

    /// <summary>Пишет факт раскрытия в журнал и поднимает алерт при аномалии по IpHash.</summary>
    Task RecordAsync(Guid listingId, Guid? viewerUserId, string ipHash, CancellationToken ct);
}

public sealed class ContactRevealService(
    AppDbContext db,
    IMemoryCache cache,
    IIpHasher ipHasher,
    IOptions<ContactRevealOptions> options,
    ILogger<ContactRevealService> logger) : IContactRevealService
{
    private readonly ContactRevealOptions _options = options.Value;

    // Партиция rate-limit, когда ключ хеширования IP не задан (dev без Security:IpHashKey).
    // Сырой IP при этом всё равно НЕ сохраняется.
    private const string NoKeyHash = "no-key";

    private sealed class Counter(DateTimeOffset resetAt)
    {
        public int Count;
        public DateTimeOffset ResetAt { get; } = resetAt;
    }

    public string HashIp(string? ip) => ipHasher.Hash(ip) ?? NoKeyHash;

    public RateLimitResult CheckRateLimit(string ipHash, Guid? userId)
    {
        // Партиция: авторизованный лимитируется по UserId, аноним — по IpHash.
        var (key, limit) = userId is { } uid
            ? ($"reveal:u:{uid}", _options.UserPerHour)
            : ($"reveal:ip:{ipHash}", _options.AnonPerHour);

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

    public Task DelayAnonymousAsync(CancellationToken ct)
    {
        var (min, max) = (_options.MinDelayMs, Math.Max(_options.MinDelayMs, _options.MaxDelayMs));
        var ms = Random.Shared.Next(min, max + 1);
        return Task.Delay(ms, ct);
    }

    public async Task RecordAsync(Guid listingId, Guid? viewerUserId, string ipHash, CancellationToken ct)
    {
        db.ContactReveals.Add(new ContactReveal
        {
            ListingId = listingId,
            ViewerUserId = viewerUserId,
            IpHash = ipHash
        });
        await db.SaveChangesAsync(ct);

        // Алерт: аномально много раскрытий с одного IpHash за последний час.
        var since = DateTimeOffset.UtcNow.AddHours(-1);
        var lastHour = await db.ContactReveals
            .CountAsync(r => r.IpHash == ipHash && r.CreatedAt >= since, ct);

        if (lastHour > _options.AlertThresholdPerHour)
            logger.LogWarning(
                "Аномальная активность раскрытия контактов: IpHash={IpHash} раскрыл {Count} контактов за час (порог {Threshold})",
                ipHash, lastHour, _options.AlertThresholdPerHour);
    }
}
