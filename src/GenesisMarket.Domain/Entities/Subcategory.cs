namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Подкатегория — справочник, привязанный к категории (<see cref="CatalogCategory"/>).
/// Не свободный текст: значение объявления обязано ссылаться на существующую строку.
/// Начальный набор — сид (см. SubcategoryConfiguration), дальше ведёт администратор.
/// Удаления нет: подкатегорию, на которую ссылаются объявления, можно только выключить.
/// </summary>
public class Subcategory
{
    public int Id { get; set; }

    /// <summary>Код категории, к которой относится подкатегория (FK на <c>categories.Code</c>).</summary>
    public required string Category { get; set; }

    /// <summary>Машинный ключ, уникален в пределах категории (например, "kvartiry").</summary>
    public required string Slug { get; set; }

    /// <summary>Человекочитаемое название на русском.</summary>
    public required string Name { get; set; }

    /// <summary>Выключенная не предлагается для новых объявлений; старые остаются валидными.</summary>
    public bool IsActive { get; set; } = true;

    public ICollection<Listing> Listings { get; set; } = new List<Listing>();
}
