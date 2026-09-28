using System.ComponentModel.DataAnnotations;

namespace GenesisMarket.Api.Contracts;

/// <summary>Город или категория справочника.</summary>
public record CatalogReferenceResponse(string Code, string Name, string Slug, int SortOrder, bool IsActive);

public record CatalogSubcategoryResponse(int Id, string Category, string Slug, string Name, bool IsActive);

/// <summary>
/// Все справочники каталога одним ответом — источник городов, категорий и подкатегорий
/// для фронтенда. Выключенные тоже здесь (с <c>isActive: false</c>): по ним ещё лежат
/// объявления, и их подписи нужны для отображения; в формы и фильтры они не попадают.
/// </summary>
public record CatalogReferencesResponse(
    IReadOnlyList<CatalogReferenceResponse> Cities,
    IReadOnlyList<CatalogReferenceResponse> Categories,
    IReadOnlyList<CatalogSubcategoryResponse> Subcategories);

/// <summary>
/// Новый город или категория. Код — латиница PascalCase: он навсегда становится
/// значением в API и в сохранённых поисках, поэтому потом не меняется.
/// </summary>
public record CreateCatalogReferenceRequest(
    [Required, RegularExpression("^[A-Z][A-Za-z0-9]{1,39}$",
        ErrorMessage = "Код — латиница с заглавной буквы, 2–40 символов, без пробелов")] string Code,
    [Required, StringLength(60, MinimumLength = 2)] string Name,
    [Required, RegularExpression("^[a-z0-9]+(-[a-z0-9]+)*$",
        ErrorMessage = "Адрес — строчная латиница, цифры и дефис"), MaxLength(60)] string Slug,
    int SortOrder = 0);

/// <summary>Изменение города или категории. Код не меняется; null — поле не трогаем.</summary>
public record UpdateCatalogReferenceRequest(
    [StringLength(60, MinimumLength = 2)] string? Name,
    [RegularExpression("^[a-z0-9]+(-[a-z0-9]+)*$",
        ErrorMessage = "Адрес — строчная латиница, цифры и дефис"), MaxLength(60)] string? Slug,
    int? SortOrder,
    bool? IsActive);

public record CreateSubcategoryRequest(
    [Required, MaxLength(40)] string Category,
    [Required, RegularExpression("^[a-z0-9]+(-[a-z0-9]+)*$",
        ErrorMessage = "Адрес — строчная латиница, цифры и дефис"), MaxLength(60)] string Slug,
    [Required, StringLength(60, MinimumLength = 2)] string Name);

/// <summary>Изменение подкатегории. Категорию не меняем: объявления уже привязаны к паре.</summary>
public record UpdateSubcategoryRequest(
    [StringLength(60, MinimumLength = 2)] string? Name,
    bool? IsActive);
