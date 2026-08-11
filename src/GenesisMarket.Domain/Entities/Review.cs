using GenesisMarket.Domain.Common;

namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Отзыв о продавце по конкретному объявлению. Слой доверия: сделки офлайн,
/// поэтому репутация — единственный механизм защиты покупателя.
/// Оставить отзыв можно только после реального раскрытия контактов
/// (<see cref="ContactReveal"/>) этим пользователем — это отсекает накрутку без
/// единой попытки контакта. Один отзыв на пару (AuthorId, ListingId) — уникальный
/// индекс на уровне БД.
/// </summary>
public class Review : BaseEntity
{
    /// <summary>Объявление, по которому оставлен отзыв.</summary>
    public Guid ListingId { get; set; }
    public Listing? Listing { get; set; }

    /// <summary>Автор отзыва (покупатель). Никогда не равен <see cref="TargetUserId"/>.</summary>
    public Guid AuthorId { get; set; }
    public User? Author { get; set; }

    /// <summary>
    /// Продавец, чью репутацию формирует отзыв (владелец объявления на момент создания).
    /// Денормализован из объявления, чтобы агрегат рейтинга считался по колонке без join.
    /// </summary>
    public Guid TargetUserId { get; set; }

    /// <summary>Оценка 1..5. Диапазон гарантируется CHECK-констрейнтом БД.</summary>
    public int Rating { get; set; }

    /// <summary>Текст отзыва, до 1000 символов (CHECK на уровне БД).</summary>
    public required string Text { get; set; }

    /// <summary>
    /// Скрыт модератором: не отдаётся в публичной выдаче и не влияет на
    /// <see cref="User.AverageRating"/>/<see cref="User.ReviewsCount"/> (пересчёт триггером).
    /// </summary>
    public bool IsHidden { get; set; }

    /// <summary>Модератор, скрывший отзыв. null, пока отзыв виден.</summary>
    public Guid? HiddenByUserId { get; set; }
}
