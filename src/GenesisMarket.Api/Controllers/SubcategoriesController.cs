using GenesisMarket.Api.Contracts;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Controllers;

/// <summary>Публичный справочник подкатегорий объявлений.</summary>
public class SubcategoriesController(AppDbContext db) : ApiControllerBase
{
    /// <summary>
    /// Возвращает все подкатегории в стабильном порядке. Доступен анонимам, чтобы
    /// форма создания объявления могла загрузить справочник до проверки сессии.
    /// </summary>
    [AllowAnonymous]
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SubcategoryResponse>>> GetAll(CancellationToken ct)
    {
        var subcategories = await db.Subcategories.AsNoTracking()
            .OrderBy(s => s.Category)
            .ThenBy(s => s.Name)
            .Select(s => new SubcategoryResponse(s.Id, s.Category, s.Slug, s.Name))
            .ToListAsync(ct);

        return Ok(subcategories);
    }
}
