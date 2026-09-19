using GenesisMarket.Api.Listings;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Moderation;

/// <summary>
/// Рыночная цена для карточки модератора: медиана фиксированных цен активных и
/// проданных объявлений той же подкатегории за окно <see cref="ModerationOptions.MarketWindowDays"/>.
/// Медиана, а не среднее: одно объявление «iPhone за 1 рубль» среднее уносит, медиану — нет.
/// Это подсказка модератору, а не правило: на решение политики модерации не влияет.
/// </summary>
public static class MarketPrice
{
    public sealed record Estimate(decimal? Median, int Sample);

    /// <summary>Строка ответа SQL (имена колонок — как в запросе).</summary>
    private sealed class Row
    {
        public decimal? Median { get; init; }
        public int Sample { get; init; }
    }

    public static async Task<Estimate> EstimateAsync(
        AppDbContext db, int subcategoryId, Guid excludeListingId, ModerationOptions options, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-options.MarketWindowDays);

        // percentile_cont в LINQ не выражается — считаем в Postgres. Параметры — через
        // интерполяцию FormattableString (EF превращает их в параметры запроса, не в текст).
        var row = await db.Database.SqlQuery<Row>($"""
            SELECT percentile_cont(0.5) WITHIN GROUP (ORDER BY "Price")::numeric AS "Median",
                   count(*)::int AS "Sample"
            FROM listings
            WHERE "SubcategoryId" = {subcategoryId}
              AND "Id" <> {excludeListingId}
              AND "DeletedAt" IS NULL
              AND "Price" IS NOT NULL
              AND "PriceType" = 'fixed'
              AND "Status" IN ('active', 'sold')
              AND "PublishedAt" >= {since}
            """).FirstAsync(ct);

        return row.Sample < options.MarketMinSample
            ? new Estimate(null, row.Sample)
            : new Estimate(row.Median is { } m ? Math.Round(m, 0) : null, row.Sample);
    }
}
