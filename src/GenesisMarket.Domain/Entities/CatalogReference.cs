namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Запись справочника каталога (город или категория). Управляется администратором.
///
/// <see cref="Code"/> — первичный ключ и значение в API («Tiraspol», «RealEstate»): на
/// него ссылаются объявления, профили, подкатегории, сохранённые поиски и query-строки
/// фронтенда, поэтому он неизменяем. Меняются только подпись, адрес для SEO, порядок и
/// активность. Удаления нет: справочник, на который ссылаются объявления, можно только
/// выключить (<see cref="IsActive"/> = false — не предлагается для новых объявлений,
/// но старые остаются валидными).
/// </summary>
public abstract class CatalogReference
{
    /// <summary>Латиница PascalCase, 2–40 символов. Неизменяем.</summary>
    public required string Code { get; set; }

    /// <summary>Подпись на русском («Тирасполь», «Недвижимость»).</summary>
    public required string Name { get; set; }

    /// <summary>Сегмент адреса для посадочных страниц: строчная латиница, цифры, дефис. Уникален.</summary>
    public required string Slug { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>Город (таблица <c>cities</c>).</summary>
public class CatalogCity : CatalogReference;

/// <summary>Категория каталога (таблица <c>categories</c>).</summary>
public class CatalogCategory : CatalogReference;
