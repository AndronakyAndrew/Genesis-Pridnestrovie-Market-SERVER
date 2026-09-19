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

    /// <summary>
    /// Исчерпал ли аккаунт часовую квоту раскрытий. null — нет, можно раскрывать;
    /// иначе сколько секунд ждать (для <c>Retry-After</c>).
    /// </summary>
    Task<int?> QuotaRetryAfterAsync(Guid viewerUserId, CancellationToken ct);

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

    public string HashIp(string? ip) => ipHasher.HashForJournal(ip);

    public Task DelayAnonymousAsync(CancellationToken ct)
    {
        var (min, max) = (_options.MinDelayMs, Math.Max(_options.MinDelayMs, _options.MaxDelayMs));
        var ms = Random.Shared.Next(min, max + 1);
        return Task.Delay(ms, ct);
    }

    /// <summary>
    /// Квота аккаунта — по журналу в БД, а не по счётчику в памяти процесса.
    /// Встроенный RateLimiter обнуляется при каждом рестарте контейнера (деплой,
    /// OOM, `up -d api`), то есть суточного потолка у него нет вовсе: достаточно
    /// дождаться перезапуска. Журнал рестарт переживает.
    ///
    /// Окно скользящее (последние 60 минут), а не календарный час: у фиксированного
    /// окна на стыке проходит двойной всплеск.
    ///
    /// Анонимы намеренно остались на middleware-лимитере: их ключ — IP, который
    /// меняется бесплатно (мобильный интернет, VPN), так что персистентность ничего
    /// не добавляет. Аккаунт же после обязательного подтверждения почты — ресурс
    /// дорогой, и именно его квоту имеет смысл считать честно.
    /// </summary>
    public async Task<int?> QuotaRetryAfterAsync(Guid viewerUserId, CancellationToken ct)
    {
        var window = TimeSpan.FromHours(1);
        var since = DateTimeOffset.UtcNow - window;

        var recent = db.ContactReveals
            .AsNoTracking()
            .Where(r => r.ViewerUserId == viewerUserId && r.CreatedAt >= since);

        if (await recent.CountAsync(ct) < _options.UserPerHour)
            return null;

        // Ждать до истечения самого старого раскрытия в окне — тогда освободится
        // ровно один слот. Пустой выборки здесь быть не может (счётчик уже >= лимита),
        // но на всякий случай откатываемся на полное окно.
        var oldest = await recent.MinAsync(r => (DateTimeOffset?)r.CreatedAt, ct);
        var retryAfter = oldest is { } o ? o + window - DateTimeOffset.UtcNow : window;
        return (int)Math.Max(1, Math.Ceiling(retryAfter.TotalSeconds));
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
