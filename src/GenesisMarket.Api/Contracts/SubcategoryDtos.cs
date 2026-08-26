using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Contracts;

/// <summary>Строка публичного справочника подкатегорий для формы и фильтров.</summary>
public record SubcategoryResponse(
    int Id,
    Category Category,
    string Slug,
    string Name);
