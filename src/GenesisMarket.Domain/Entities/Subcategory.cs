using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Подкатегория — справочник, привязанный к <see cref="Enums.Category"/>.
/// Не свободный текст: значение объявления обязано ссылаться на существующую строку.
/// Наполняется сидом (см. SubcategoryConfiguration).
/// </summary>
public class Subcategory
{
    public int Id { get; set; }

    /// <summary>Категория, к которой относится подкатегория.</summary>
    public Category Category { get; set; }

    /// <summary>Машинный ключ, уникален в пределах категории (например, "kvartiry").</summary>
    public required string Slug { get; set; }

    /// <summary>Человекочитаемое название на русском.</summary>
    public required string Name { get; set; }

    public ICollection<Listing> Listings { get; set; } = new List<Listing>();
}
