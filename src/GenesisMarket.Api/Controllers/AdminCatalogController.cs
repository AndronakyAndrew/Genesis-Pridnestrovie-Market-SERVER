using GenesisMarket.Api.Audit;
using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Business;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Listings;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Управление справочниками каталога — уровень Admin (<see cref="Permissions.CatalogManage"/>).
/// Удаления нет ни у одного справочника: на записи ссылаются объявления, профили и
/// сохранённые поиски, поэтому запись можно только выключить. Код города и категории
/// неизменяем. Каждое изменение — в журнал действий (что было и что стало) и сброс
/// кэша справочника.
/// </summary>
[Route("api/admin/catalog")]
[Authorize(Policy = Permissions.CatalogManage)]
public class AdminCatalogController(
    AppDbContext db,
    ICatalogReferences references,
    IAuditLog auditLog) : ApiControllerBase
{
    // ---- Города ----

    [HttpPost("cities")]
    public Task<ActionResult<CatalogReferenceResponse>> CreateCity(CreateCatalogReferenceRequest request, CancellationToken ct) =>
        CreateAsync(db.Cities, new CatalogCity { Code = request.Code, Name = request.Name.Trim(), Slug = request.Slug, SortOrder = request.SortOrder },
            AuditActions.TargetCity, ct);

    [HttpPatch("cities/{code}")]
    public Task<ActionResult<CatalogReferenceResponse>> UpdateCity(string code, UpdateCatalogReferenceRequest request, CancellationToken ct) =>
        UpdateAsync(db.Cities, code, request, AuditActions.TargetCity, ct);

    // ---- Категории ----

    [HttpPost("categories")]
    public Task<ActionResult<CatalogReferenceResponse>> CreateCategory(CreateCatalogReferenceRequest request, CancellationToken ct) =>
        CreateAsync(db.Categories, new CatalogCategory { Code = request.Code, Name = request.Name.Trim(), Slug = request.Slug, SortOrder = request.SortOrder },
            AuditActions.TargetCategory, ct);

    [HttpPatch("categories/{code}")]
    public Task<ActionResult<CatalogReferenceResponse>> UpdateCategory(string code, UpdateCatalogReferenceRequest request, CancellationToken ct) =>
        UpdateAsync(db.Categories, code, request, AuditActions.TargetCategory, ct);

    // ---- Подкатегории ----

    [HttpPost("subcategories")]
    public async Task<ActionResult<CatalogSubcategoryResponse>> CreateSubcategory(
        CreateSubcategoryRequest request, CancellationToken ct)
    {
        var catalog = await references.GetAsync(ct);
        if (catalog.Category(request.Category) is not { } category)
            return Problem(title: "Категория не найдена", statusCode: StatusCodes.Status400BadRequest);

        var entity = new Subcategory { Category = category.Code, Slug = request.Slug, Name = request.Name.Trim() };
        db.Subcategories.Add(entity);

        // Id — из identity, поэтому запись журнала ставится после вставки, в той же транзакции.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await SaveUniqueAsync("Такой адрес уже есть в этой категории", ct) is { } conflict)
            return conflict;

        auditLog.Record(AuditActions.CatalogCreate, AuditActions.TargetSubcategory, entity.Id.ToString(),
            newValue: Snapshot(entity));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        references.Invalidate();

        return Ok(Map(entity));
    }

    [HttpPatch("subcategories/{id:int}")]
    public async Task<ActionResult<CatalogSubcategoryResponse>> UpdateSubcategory(
        int id, UpdateSubcategoryRequest request, CancellationToken ct)
    {
        var entity = await db.Subcategories.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entity is null)
            return Problem(title: "Подкатегория не найдена", statusCode: StatusCodes.Status404NotFound);

        var before = Snapshot(entity);
        if (request.Name is { } name) entity.Name = name.Trim();
        if (request.IsActive is { } active) entity.IsActive = active;

        var after = Snapshot(entity);
        if (before == after)
            return Ok(Map(entity));

        auditLog.Record(AuditActions.CatalogUpdate, AuditActions.TargetSubcategory, id.ToString(),
            oldValue: before, newValue: after);
        await db.SaveChangesAsync(ct);
        references.Invalidate();

        return Ok(Map(entity));
    }

    // ---- helpers ----

    private async Task<ActionResult<CatalogReferenceResponse>> CreateAsync<T>(
        DbSet<T> set, T entity, string targetType, CancellationToken ct) where T : CatalogReference
    {
        // Регистр кода в API не важен, значит и «tiraspol» рядом с «Tiraspol» — дубль.
        var lower = entity.Code.ToLowerInvariant();
        if (await set.AnyAsync(r => r.Code.ToLower() == lower, ct))
            return Problem(title: "Запись с таким кодом уже есть", statusCode: StatusCodes.Status409Conflict);

        set.Add(entity);
        auditLog.Record(AuditActions.CatalogCreate, targetType, entity.Code, newValue: Snapshot(entity));

        if (await SaveUniqueAsync("Такой код или адрес уже заняты", ct) is { } conflict)
            return conflict;
        references.Invalidate();

        return Ok(Map(entity));
    }

    private async Task<ActionResult<CatalogReferenceResponse>> UpdateAsync<T>(
        DbSet<T> set, string code, UpdateCatalogReferenceRequest request, string targetType, CancellationToken ct)
        where T : CatalogReference
    {
        var lower = code.ToLowerInvariant();
        var entity = await set.FirstOrDefaultAsync(r => r.Code.ToLower() == lower, ct);
        if (entity is null)
            return Problem(title: "Запись не найдена", statusCode: StatusCodes.Status404NotFound);

        var before = Snapshot(entity);
        if (request.Name is { } name) entity.Name = name.Trim();
        if (request.Slug is { } slug) entity.Slug = slug;
        if (request.SortOrder is { } order) entity.SortOrder = order;
        if (request.IsActive is { } active) entity.IsActive = active;

        var after = Snapshot(entity);
        if (before == after)
            return Ok(Map(entity));

        entity.UpdatedAt = DateTimeOffset.UtcNow;
        auditLog.Record(AuditActions.CatalogUpdate, targetType, entity.Code, oldValue: before, newValue: after);

        if (await SaveUniqueAsync("Такой адрес уже занят", ct) is { } conflict)
            return conflict;
        references.Invalidate();

        return Ok(Map(entity));
    }

    /// <summary>Сохранение с переводом нарушения уникальности (гонка двух админов) в 409.</summary>
    private async Task<ObjectResult?> SaveUniqueAsync(string conflictTitle, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
        {
            return Problem(title: conflictTitle, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private sealed record ReferenceSnapshot(string Name, string Slug, int SortOrder, bool IsActive);

    private sealed record SubcategorySnapshot(string Category, string Slug, string Name, bool IsActive);

    private static ReferenceSnapshot Snapshot(CatalogReference r) => new(r.Name, r.Slug, r.SortOrder, r.IsActive);

    private static SubcategorySnapshot Snapshot(Subcategory s) => new(s.Category, s.Slug, s.Name, s.IsActive);

    private static CatalogReferenceResponse Map(CatalogReference r) =>
        new(r.Code, r.Name, r.Slug, r.SortOrder, r.IsActive);

    private static CatalogSubcategoryResponse Map(Subcategory s) =>
        new(s.Id, s.Category, s.Slug, s.Name, s.IsActive);
}
