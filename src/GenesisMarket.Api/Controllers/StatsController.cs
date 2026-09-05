using GenesisMarket.Api.Contracts;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GenesisMarket.Api.Controllers;

/// <summary>Публичные счётчики площадки для витрины (главная страница).</summary>
public class StatsController(AppDbContext db, IMemoryCache cache) : ApiControllerBase
{
    private const string CacheKey = "public-stats";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Число пользователей и опубликованных объявлений. Доступен анонимам: счётчики
    /// показываются на главной до входа. Два COUNT по всей таблице дороги, поэтому
    /// результат кэшируется на 60 секунд (как и <c>/api/listings/count</c>).
    ///
    /// В пользователей входят только рядовые аккаунты: персонал площадки
    /// (<see cref="UserRole.Admin"/>, <see cref="UserRole.Moderator"/>) — не участники
    /// сделок и витринный счётчик завышают. Удалённые (анонимизированные) аккаунты
    /// тоже не считаются: строка остаётся в БД, но пользователя за ней уже нет.
    /// </summary>
    [AllowAnonymous]
    [HttpGet]
    public async Task<ActionResult<PublicStatsResponse>> Get(CancellationToken ct)
    {
        if (!cache.TryGetValue(CacheKey, out PublicStatsResponse? stats) || stats is null)
        {
            var usersCount = await db.Users.AsNoTracking()
                .Where(u => u.Role == UserRole.User && !u.IsDeleted)
                .LongCountAsync(ct);

            // Глобальный фильтр Listing отсекает мягко удалённые.
            var listingsCount = await db.Listings.AsNoTracking()
                .Where(l => l.Status == ListingStatus.Active)
                .LongCountAsync(ct);

            stats = new PublicStatsResponse(usersCount, listingsCount);
            cache.Set(CacheKey, stats, CacheTtl);
        }

        return Ok(stats);
    }
}
