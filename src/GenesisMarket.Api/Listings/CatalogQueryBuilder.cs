using System.Globalization;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Listings;

/// <summary>Белый список сортировок каталога. ORDER BY не собирается из строки запроса.</summary>
public enum CatalogSort
{
    New,
    PriceAsc,
    PriceDesc,
    Popular
}

/// <summary>
/// Проекция строки каталога на уровне БД. Помимо полей карточки тянет поля,
/// нужные для формирования курсора (CreatedAt, ViewsCount) — без загрузки сущности
/// и без Include всей коллекции изображений (первое фото — коррелированным подзапросом).
/// </summary>
public sealed record CatalogRow(
    Guid Id,
    string Slug,
    string Title,
    decimal? Price,
    PriceType PriceType,
    City City,
    Category Category,
    string? FirstImageUrl,
    DateTimeOffset? PublishedAt,
    DateTimeOffset CreatedAt,
    int ViewsCount)
{
    // Продвижение (bump) — шаг 7. Пока в каталоге нет продвинутых объявлений,
    // поэтому флаг всегда false; когда появится, здесь будет реальное условие.
    public ListingCardResponse ToCard() => new(
        Id, Slug, Title, Price, PriceType, City, Category, FirstImageUrl, PublishedAt, IsBumped: false);
}

/// <summary>
/// Построение запроса каталога: фильтры, keyset-пагинация и сортировка.
/// Всё — трансляцией в SQL (никакой материализации до Take), с AsNoTracking у вызывающего.
/// </summary>
public static class CatalogQueryBuilder
{
    /// <summary>Разбор токена сортировки из белого списка. null ⇒ невалидный явный токен (400).</summary>
    public static CatalogSort? ParseSort(string? token) => token switch
    {
        null or "" or "new" => CatalogSort.New,
        "price_asc" => CatalogSort.PriceAsc,
        "price_desc" => CatalogSort.PriceDesc,
        "popular" => CatalogSort.Popular,
        _ => null
    };

    /// <summary>Обратный токен для записи в курсор.</summary>
    public static string Token(CatalogSort sort) => sort switch
    {
        CatalogSort.PriceAsc => "price_asc",
        CatalogSort.PriceDesc => "price_desc",
        CatalogSort.Popular => "popular",
        _ => "new"
    };

    /// <summary>
    /// Фильтры каталога. Только Active — Sold/Archived/PendingReview/Rejected и черновики
    /// не попадают в каталог никогда, даже для владельца (владелец здесь не учитывается).
    /// </summary>
    public static IQueryable<Listing> Filter(IQueryable<Listing> source, CatalogQuery q)
    {
        var query = source.Where(l => l.Status == ListingStatus.Active);

        if (q.Category is { } category)
            query = query.Where(l => l.Category == category);

        if (q.Subcategory is { } subcategoryId)
            query = query.Where(l => l.SubcategoryId == subcategoryId);

        if (q.Cities is { Count: > 0 } cities)
            query = query.Where(l => cities.Contains(l.City));

        if (q.PriceFrom is { } from)
            query = query.Where(l => l.Price != null && l.Price >= from);

        if (q.PriceTo is { } to)
            query = query.Where(l => l.Price != null && l.Price <= to);

        if (q.Condition is { } condition)
            query = query.Where(l => l.Condition == condition);

        if (q.PriceType is { } priceType)
            query = query.Where(l => l.PriceType == priceType);

        return query;
    }

    /// <summary>
    /// Keyset-предикат «строки строго после курсора» для выбранной сортировки.
    /// Id — тайбрейкер, поэтому порядок детерминирован при равных значениях поля.
    /// </summary>
    public static IQueryable<Listing> ApplyKeyset(
        IQueryable<Listing> query, CatalogSort sort, string sortKey, Guid afterId)
    {
        switch (sort)
        {
            case CatalogSort.New:
            {
                var created = new DateTimeOffset(long.Parse(sortKey, CultureInfo.InvariantCulture), TimeSpan.Zero);
                return query.Where(l =>
                    l.CreatedAt < created ||
                    (l.CreatedAt == created && l.Id.CompareTo(afterId) < 0));
            }
            case CatalogSort.Popular:
            {
                var views = int.Parse(sortKey, CultureInfo.InvariantCulture);
                return query.Where(l =>
                    l.ViewsCount < views ||
                    (l.ViewsCount == views && l.Id.CompareTo(afterId) < 0));
            }
            case CatalogSort.PriceAsc:
            {
                if (sortKey == "n")
                    // Курсор на договорной цене: дальше — только другие NULL с большим Id.
                    return query.Where(l => l.Price == null && l.Id.CompareTo(afterId) > 0);

                var price = decimal.Parse(sortKey, CultureInfo.InvariantCulture);
                return query.Where(l =>
                    (l.Price != null && l.Price > price) ||
                    (l.Price != null && l.Price == price && l.Id.CompareTo(afterId) > 0) ||
                    l.Price == null); // NULL (договорные) идут в самый конец
            }
            case CatalogSort.PriceDesc:
            {
                if (sortKey == "n")
                    return query.Where(l => l.Price == null && l.Id.CompareTo(afterId) > 0);

                var price = decimal.Parse(sortKey, CultureInfo.InvariantCulture);
                return query.Where(l =>
                    (l.Price != null && l.Price < price) ||
                    (l.Price != null && l.Price == price && l.Id.CompareTo(afterId) > 0) ||
                    l.Price == null);
            }
            default:
                return query;
        }
    }

    /// <summary>ORDER BY для сортировки. Договорные цены (NULL) всегда в конце.</summary>
    public static IOrderedQueryable<Listing> ApplyOrder(IQueryable<Listing> query, CatalogSort sort) => sort switch
    {
        CatalogSort.New => query
            .OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id),
        CatalogSort.Popular => query
            .OrderByDescending(l => l.ViewsCount).ThenByDescending(l => l.Id),
        CatalogSort.PriceAsc => query
            .OrderBy(l => l.Price == null).ThenBy(l => l.Price).ThenBy(l => l.Id),
        CatalogSort.PriceDesc => query
            .OrderBy(l => l.Price == null).ThenByDescending(l => l.Price).ThenBy(l => l.Id),
        _ => query.OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id)
    };

    /// <summary>Значение поля сортировки последней строки страницы — для следующего курсора.</summary>
    public static string NextKey(CatalogSort sort, CatalogRow row) => sort switch
    {
        CatalogSort.New => row.CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture),
        CatalogSort.Popular => row.ViewsCount.ToString(CultureInfo.InvariantCulture),
        _ => row.Price is { } p ? p.ToString(CultureInfo.InvariantCulture) : "n"
    };
}
