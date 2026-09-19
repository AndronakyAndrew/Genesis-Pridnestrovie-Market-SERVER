using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Listings;

/// <summary>
/// Кому видно объявление по прямой ссылке. Живёт отдельно от контроллеров, потому
/// что правило нужно в двух местах (карточка и список фотографий), а разъехавшись,
/// такие правила открывают ровно то, что закрывали.
/// </summary>
public static class ListingVisibility
{
    /// <summary>
    /// Статусы, у которых публичного адреса никогда не было: черновик, ожидание
    /// проверки, отказ. <see cref="ListingStatus.Sold"/> и
    /// <see cref="ListingStatus.Archived"/> сюда НЕ входят: на них ссылаются из
    /// переписки и поисковиков, и они намеренно остаются доступными по прямой ссылке.
    /// </summary>
    private static readonly ListingStatus[] NonPublic =
        [ListingStatus.Draft, ListingStatus.PendingReview, ListingStatus.Rejected];

    /// <summary>Опубликовано ли — то есть виден ли посторонним сам факт объявления.</summary>
    public static bool IsPublic(ListingStatus status) => !NonPublic.Contains(status);

    /// <summary>
    /// Вправе ли текущий запрос видеть объявление. Неопубликованное — только
    /// владельцу и модератору; всем остальным вызывающий отдаёт 404 (не 403:
    /// постороннему незачем знать, что объявление существует).
    /// </summary>
    public static bool CanSee(ListingStatus status, Guid ownerId, Guid? currentUserId, UserRole? role) =>
        IsPublic(status)
        || (currentUserId is { } id && id == ownerId)
        || role is UserRole.Moderator or UserRole.Admin;
}
