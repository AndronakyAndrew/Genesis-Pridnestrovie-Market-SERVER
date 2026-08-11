using System.ComponentModel.DataAnnotations;

namespace GenesisMarket.Api.Contracts;

/// <summary>
/// Создание отзыва о продавце по объявлению. Целевой пользователь (продавец)
/// определяется сервером по владельцу объявления — клиент его не задаёт.
/// </summary>
public record CreateReviewRequest(
    [Required] Guid ListingId,
    [Range(1, 5)] int Rating,
    [Required, MinLength(1), MaxLength(1000)] string Text);

/// <summary>Редактирование отзыва (в течение 24 часов после публикации).</summary>
public record UpdateReviewRequest(
    [Range(1, 5)] int Rating,
    [Required, MinLength(1), MaxLength(1000)] string Text);

/// <summary>
/// Отзыв в публичной выдаче. Автор представлен именем/аватаром из профиля,
/// не email. <see cref="IsEditable"/> — можно ли редактировать (автор + окно 24ч).
/// </summary>
public record ReviewResponse(
    Guid Id,
    Guid ListingId,
    Guid AuthorId,
    string AuthorName,
    string? AuthorAvatarUrl,
    int Rating,
    string Text,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    bool IsEditable);

/// <summary>Страница отзывов о продавце (курсорная пагинация, скрытые не отдаются).</summary>
public record ReviewsPageResponse(
    IReadOnlyList<ReviewResponse> Items,
    string? NextCursor,
    bool HasMore);
