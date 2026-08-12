using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Contracts;

/// <summary>Ответ по объявлению. Возвращается вместо сущности EF.</summary>
public record ListingResponse(
    Guid Id,
    string Slug,
    string Title,
    string Description,
    decimal? Price,
    PriceType PriceType,
    Category Category,
    int SubcategoryId,
    City City,
    string? District,
    Condition Condition,
    ListingStatus Status,
    int ViewsCount,
    Guid OwnerId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt,
    int ContactRevealCount = 0,
    /// <summary>Денормализованный счётчик добавлений в избранное.</summary>
    int FavoritesCount = 0,
    /// <summary>Объявление в избранном у текущего пользователя (false для анонима).</summary>
    bool IsFavorite = false,
    /// <summary>
    /// Сколько дней до автоархивации (от coalesce(BumpedAt, PublishedAt) + срок из конфигурации).
    /// Заполняется только для Active-объявлений; для остальных статусов — null.
    /// </summary>
    int? DaysUntilArchive = null);

/// <summary>
/// Создание объявления. Валидируется FluentValidation (см. CreateListingRequestValidator).
/// <see cref="Publish"/> = true — сразу отправить на публикацию (иначе черновик).
/// </summary>
public record CreateListingRequest(
    string Title,
    string Description,
    decimal? Price,
    PriceType PriceType,
    Category Category,
    int SubcategoryId,
    City City,
    string? District,
    Condition Condition,
    bool Publish = false);

/// <summary>
/// Редактирование объявления. ФИКСИРОВАННЫЙ набор редактируемых полей:
/// Status, UserId/OwnerId, ViewsCount, Slug, CreatedAt, PublishedAt сюда не входят
/// и через PATCH не меняются.
/// </summary>
public record UpdateListingRequest(
    string Title,
    string Description,
    decimal? Price,
    PriceType PriceType,
    Category Category,
    int SubcategoryId,
    City City,
    string? District,
    Condition Condition);
