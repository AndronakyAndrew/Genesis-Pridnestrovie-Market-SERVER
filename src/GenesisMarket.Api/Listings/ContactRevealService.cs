using GenesisMarket.Api.Auth;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Listings;

/// <summary>
/// Анти-скрейпинг раскрытия контактов: хеширование IP, намеренная задержка анонимам,
/// журналирование каждого раскрытия и алерт при аномальной активности. Сам rate-limit
/// (аноним по IP, авторизованный по пользователю) вынесен на встроенный RateLimiter —
/// политику "contact" (см. <c>RateLimitingSetup</c>).
/// </summary>
public interface IContactRevealService
{
    /// <summary>HMAC-SHA256 от IP (hex). Значение для журнала раскрытий.</summary>
    string HashIp(string? ip);

    /// <summary>Задержка ответа анонимам (случайная), чтобы массовый обход был дороже.</summary>
    Task DelayAnonymousAsync(CancellationToken ct);

    /// <summary>Пишет факт раскрытия в журнал и поднимает алерт при аномалии по IpHash.</summary>
    Task RecordAsync(Guid listingId, Guid? viewerUserId, string ipHash, CancellationToken ct);
}

public sealed class ContactRevealService(
    AppDbContext db,
    IIpHasher ipHasher,
    IOptions<ContactRevealOptions> options,
    ILogger<ContactRevealService> logger) : IContactRevealService
{
    private readonly ContactRevealOptions _options = options.Value;

    // Значение IpHash для журнала, когда ключ хеширования IP не задан (dev без Security:IpHashKey).
    // Сырой IP при этом всё равно НЕ сохраняется.
    private const string NoKeyHash = "no-key";

    public string HashIp(string? ip) => ipHasher.Hash(ip) ?? NoKeyHash;

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
