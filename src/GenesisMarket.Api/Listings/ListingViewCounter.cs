using GenesisMarket.Api.Auth;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Listings;

/// <summary>
/// Счётчик просмотров. Увеличивает отдельным атомарным UPDATE
/// (SET "ViewsCount" = "ViewsCount" + 1), не чаще одного раза за окно
/// на пару (ListingId, IpHash).
/// </summary>
public interface IListingViewCounter
{
    Task RegisterAsync(Guid listingId, string? ip, CancellationToken ct);
}

public sealed class ListingViewCounter(
    AppDbContext db,
    IMemoryCache cache,
    IIpHasher ipHasher,
    IOptions<ListingOptions> options) : IListingViewCounter
{
    private readonly int _throttleMinutes = options.Value.ViewThrottleMinutes;

    public async Task RegisterAsync(Guid listingId, string? ip, CancellationToken ct)
    {
        // Без ключа хеширования кладём общий литерал, а НЕ сырой адрес: в Production
        // ключ обязателен (старт падает без него), так что там поведение не меняется,
        // а в dev незачем держать IP даже в ключе кэша в памяти. Цена — на время окна
        // просмотры всех анонимов в dev считаются за один.
        var ipHash = ipHasher.Hash(ip) ?? IpHasherExtensions.NoKeyHash;
        var key = $"view:{listingId}:{ipHash}";

        // В пределах окна повторный просмотр не засчитываем.
        if (cache.TryGetValue(key, out _))
            return;
        cache.Set(key, true, TimeSpan.FromMinutes(_throttleMinutes));

        // Атомарный инкремент на уровне SQL, без read-modify-write.
        await db.Listings
            .Where(l => l.Id == listingId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.ViewsCount, l => l.ViewsCount + 1), ct);
    }
}
