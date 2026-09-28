using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Listings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GenesisMarket.Api.Controllers;

/// <summary>Публичные справочники каталога: города, категории, подкатегории.</summary>
[AllowAnonymous]
[Route("api/catalog/references")]
public class CatalogReferencesController(ICatalogReferences references) : ApiControllerBase
{
    /// <summary>
    /// Всё одним ответом. Кэшируется и на сервере (снимок), и у клиента: справочник
    /// меняется редко, а нужен на каждой странице каталога.
    /// </summary>
    [HttpGet]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
    public async Task<ActionResult<CatalogReferencesResponse>> Get(CancellationToken ct)
    {
        var catalog = await references.GetAsync(ct);
        return Ok(new CatalogReferencesResponse(
            catalog.Cities.Select(Map).ToList(),
            catalog.Categories.Select(Map).ToList(),
            catalog.Subcategories
                .Select(s => new CatalogSubcategoryResponse(s.Id, s.Category, s.Slug, s.Name, s.IsActive))
                .ToList()));
    }

    internal static CatalogReferenceResponse Map(CatalogEntry e) =>
        new(e.Code, e.Name, e.Slug, e.SortOrder, e.IsActive);
}
