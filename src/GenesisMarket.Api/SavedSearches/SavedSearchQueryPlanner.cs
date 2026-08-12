using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Listings;
using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.SavedSearches;

/// <summary>
/// Единая точка построения запроса по критериям сохранённого поиска — тем же билдером,
/// что и живой каталог (<see cref="CatalogQueryBuilder"/>). Гарантирует, что фон и витрина
/// отбирают объявления одинаково.
/// </summary>
public static class SavedSearchQueryPlanner
{
    /// <summary>
    /// Базовый отбор: только Active + все фильтры + опциональный полнотекстовый поиск.
    /// Ровно то же, что делает каталог для этих же параметров.
    /// </summary>
    public static IQueryable<Listing> Filter(IQueryable<Listing> source, SavedSearchQuery query)
    {
        var filtered = CatalogQueryBuilder.Filter(source, query.ToCatalogQuery());

        var text = CatalogQueryBuilder.NormalizeText(query.Q);
        if (text is not null)
            filtered = CatalogQueryBuilder.ApplyTextSearch(filtered, text);

        return filtered;
    }

    /// <summary>
    /// Id самого свежего объявления, подходящего под критерии (по паре PublishedAt, Id).
    /// Служит начальным курсором при создании/изменении поиска: подписчик получает
    /// уведомления только о том, что опубликовано ПОСЛЕ, а не рассылку по всему каталогу.
    /// null ⇒ подходящих объявлений сейчас нет (курсор с «начала» — любое будущее совпадение новое).
    /// </summary>
    public static async Task<Guid?> AnchorAsync(
        IQueryable<Listing> source, SavedSearchQuery query, CancellationToken ct)
    {
        var newest = await Filter(source, query)
            .Where(l => l.PublishedAt != null)
            .OrderByDescending(l => l.PublishedAt).ThenByDescending(l => l.Id)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefaultAsync(ct);

        return newest;
    }
}
