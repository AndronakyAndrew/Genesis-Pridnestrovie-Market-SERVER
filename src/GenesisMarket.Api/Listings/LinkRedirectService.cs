using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Seo;
using GenesisMarket.Api.Telegram;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Listings;

/// <summary>
/// Белый список источников перехода. В <c>link_clicks.Source</c> попадает только значение
/// отсюда — сырой query-параметр в БД не пишется никогда.
/// </summary>
public static class LinkSources
{
    /// <summary>Пост в Telegram-канале.</summary>
    public const string Telegram = "tg";

    /// <summary>Параметр не задан или не из белого списка.</summary>
    public const string Unknown = "unknown";

    private static readonly string[] Allowed = [Telegram];

    /// <summary>Каноническое значение из белого списка либо <see cref="Unknown"/>.</summary>
    public static string Normalize(string? raw)
    {
        foreach (var source in Allowed)
            if (string.Equals(source, raw, StringComparison.OrdinalIgnoreCase))
                return source;

        return Unknown;
    }
}

/// <summary>
/// Короткая ссылка на объявление для внешних площадок (<c>/r/l/{id}?s=tg</c>): решает, куда
/// вести, и учитывает переход. Пользователь важнее метрики — сбой БД при чтении или записи
/// клика не мешает редиректу, а только пишется в лог.
/// </summary>
public interface ILinkRedirectService
{
    /// <summary>Абсолютный адрес для 302: карточка активного объявления либо главная.</summary>
    Task<string> ResolveListingAsync(Guid listingId, string? source, string? ip, CancellationToken ct);
}

public sealed class LinkRedirectService(
    AppDbContext db,
    IIpHasher ipHasher,
    IMemoryCache cache,
    IConfiguration configuration,
    IOptions<ListingOptions> options,
    ILogger<LinkRedirectService> logger) : ILinkRedirectService
{
    // Метки фиксированы: по ним трафик из канала отделяется в аналитике фронтенда.
    private const string Utm = "?utm_source=telegram&utm_medium=channel&utm_campaign=listing";

    // Потолок ожидания записи клика: зависшая БД не должна держать редирект.
    private static readonly TimeSpan RecordTimeout = TimeSpan.FromSeconds(2);

    private readonly int _throttleSeconds = options.Value.ClickThrottleSeconds;

    public async Task<string> ResolveListingAsync(Guid listingId, string? source, string? ip, CancellationToken ct)
    {
        var baseUrl = SiteBaseUrl();

        string? slug;
        try
        {
            // Мягко удалённые отсекает глобальный query filter; «снятые» (Sold/Archived/…) — статус.
            slug = await db.Listings.AsNoTracking()
                .Where(l => l.Id == listingId && l.Status == ListingStatus.Active)
                .Select(l => l.Slug)
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Редирект на объявление {ListingId}: БД недоступна, ведём на главную", listingId);
            return SeoUrls.Home(baseUrl);
        }

        if (slug is null)
            return SeoUrls.Home(baseUrl);

        await TryRecordClickAsync(listingId, LinkSources.Normalize(source), ip, ct);

        // Карточка на фронтенде — /listing/{slug} (см. SeoUrls), а не /listings/{id}: такого маршрута нет.
        return SeoUrls.Listing(baseUrl, slug) + Utm;
    }

    private async Task TryRecordClickAsync(Guid listingId, string source, string? ip, CancellationToken ct)
    {
        // Тот же механизм, что у журнала раскрытий контактов: HMAC от IP, сырой IP не хранится.
        var ipHash = ipHasher.HashForJournal(ip);

        if (_throttleSeconds > 0)
        {
            var key = $"link-click:{listingId}:{source}:{ipHash}";
            if (cache.TryGetValue(key, out _))
                return;
            cache.Set(key, true, TimeSpan.FromSeconds(_throttleSeconds));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RecordTimeout);

        try
        {
            db.LinkClicks.Add(new LinkClick { ListingId = listingId, Source = source, IpHash = ipHash });
            await db.SaveChangesAsync(timeout.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Несохранённая строка не должна уехать в чужой SaveChanges этого же scope.
            db.ChangeTracker.Clear();
            logger.LogError(ex,
                "Не удалось записать переход на объявление {ListingId} (источник {Source}); редирект выполняется",
                listingId, source);
        }
    }

    // Читаем одну строку напрямую, а не весь TelegramOptions: мусор в другом ключе секции
    // (Telegram__AdminChatId) ломает биндинг, а редирект от настроек бота зависеть не должен.
    private string SiteBaseUrl()
    {
        var raw = configuration[$"{TelegramOptions.SectionName}:{nameof(TelegramOptions.SiteBaseUrl)}"];

        return Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? raw!.TrimEnd('/')
            : TelegramOptions.DefaultSiteBaseUrl;
    }
}
