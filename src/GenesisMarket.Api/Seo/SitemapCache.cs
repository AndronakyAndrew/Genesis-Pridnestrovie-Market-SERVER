using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Seo;

/// <summary>
/// Кэш готовых ответов карты сайта. Краулер обходит sitemap регулярно и целиком, поэтому
/// собранный XML держим в памяти <see cref="SeoOptions.SitemapCacheTtlMinutes"/> минут —
/// в БД ходим не чаще раза за TTL. Инвалидация по событиям не нужна: карта сайта не обязана
/// быть real-time, отставание в пределах TTL допустимо.
/// </summary>
public sealed class SitemapCache(IMemoryCache cache, IOptions<SeoOptions> options)
{
    /// <summary>Общий для всех ключей замок: два краулера одновременно не пересобирают одно и то же.</summary>
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Выданные ключи — чтобы <see cref="Invalidate"/> знал, что сбрасывать.</summary>
    private readonly ConcurrentDictionary<string, byte> _keys = new();

    private TimeSpan Ttl => TimeSpan.FromMinutes(options.Value.SitemapCacheTtlMinutes);

    public const string KeyPrefix = "seo:sitemap:";

    /// <summary>Ключ кэша раздела карты сайта (для объявлений — с номером страницы).</summary>
    public static string Key(SitemapSection section, int page = 0) =>
        section == SitemapSection.Listings ? $"{KeyPrefix}listings:{page}" : $"{KeyPrefix}{section}";

    /// <summary>Ключ кэша числа активных объявлений (точный COUNT на каждый обход — дорого).</summary>
    public const string CountKey = KeyPrefix + "active-count";

    /// <summary>Ключ кэша sitemap-index.</summary>
    public const string IndexKey = KeyPrefix + "index";

    /// <summary>
    /// Значение из кэша либо пересчёт с сохранением на TTL. TTL ≤ 0 отключает кэш —
    /// удобно в разработке, когда карту хочется видеть свежей.
    /// </summary>
    public async Task<T> GetOrCreateAsync<T>(
        string key, Func<CancellationToken, Task<T>> create, CancellationToken ct)
    {
        var ttl = Ttl;
        if (ttl <= TimeSpan.Zero)
            return await create(ct);

        if (cache.TryGetValue(key, out T? cached) && cached is not null)
            return cached;

        await _lock.WaitAsync(ct);
        try
        {
            // Пока ждали замок, значение мог посчитать сосед — проверяем ещё раз.
            if (cache.TryGetValue(key, out cached) && cached is not null)
                return cached;

            var value = await create(ct);
            cache.Set(key, value, ttl);
            _keys.TryAdd(key, 0);
            return value;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Сбрасывает весь кэш карты сайта (ручной сброс и тесты).</summary>
    public void Invalidate()
    {
        foreach (var key in _keys.Keys)
        {
            cache.Remove(key);
            _keys.TryRemove(key, out _);
        }
    }
}
