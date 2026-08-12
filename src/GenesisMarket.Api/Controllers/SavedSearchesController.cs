using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Listings;
using GenesisMarket.Api.SavedSearches;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Infrastructure.Persistence;
using GenesisMarket.Infrastructure.Scheduling;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Сохранённые поиски пользователя: критерии каталога, по которым фоновый джоб находит
/// новые объявления и уведомляет автора. Критерии валидируются теми же правилами, что и
/// живой каталог; при сохранении курсор привязывается к текущему «сейчас», чтобы подписчик
/// получал только будущие совпадения, а не рассылку по всему каталогу.
/// </summary>
[Authorize]
[Route("api/saved-searches")]
public class SavedSearchesController(
    AppDbContext db,
    IOptions<SavedSearchOptions> options) : ApiControllerBase
{
    private readonly SavedSearchOptions _o = options.Value;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SavedSearchResponse>>> GetAll(CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;
        var searches = await db.SavedSearches.AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

        return Ok(searches.Select(ToResponse).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SavedSearchResponse>> GetById(Guid id, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;
        var search = await db.SavedSearches.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
        if (search is null)
            return Problem(title: "Сохранённый поиск не найден", statusCode: StatusCodes.Status404NotFound);

        return Ok(ToResponse(search));
    }

    [HttpPost]
    public async Task<ActionResult<SavedSearchResponse>> Create(
        CreateSavedSearchRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;

        if (CatalogQueryBuilder.ValidateFilters(
                request.Query.Cities, request.Query.PriceFrom, request.Query.PriceTo) is { } error)
            return Problem(title: error, statusCode: StatusCodes.Status400BadRequest);

        // Лимит активных поисков на пользователя.
        var active = await db.SavedSearches.CountAsync(s => s.UserId == userId && s.IsActive, ct);
        if (active >= _o.MaxActivePerUser)
            return Problem(
                title: $"Достигнут лимит активных сохранённых поисков ({_o.MaxActivePerUser})",
                statusCode: StatusCodes.Status409Conflict);

        // Привязываем курсор к самому свежему совпадению сейчас — уведомляем только о будущем.
        var anchor = await SavedSearchQueryPlanner.AnchorAsync(db.Listings.AsNoTracking(), request.Query, ct);

        var search = new SavedSearch
        {
            UserId = userId,
            Name = request.Name.Trim(),
            QueryJson = SavedSearchJson.Serialize(request.Query),
            NotifyChannel = request.NotifyChannel,
            LastNotifiedListingId = anchor,
            IsActive = true
        };
        db.SavedSearches.Add(search);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetById), new { id = search.Id }, ToResponse(search));
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<SavedSearchResponse>> Update(
        Guid id, UpdateSavedSearchRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;

        var search = await db.SavedSearches.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
        if (search is null)
            return Problem(title: "Сохранённый поиск не найден", statusCode: StatusCodes.Status404NotFound);

        if (request.Name is { } name)
            search.Name = name.Trim();

        if (request.NotifyChannel is { } channel)
            search.NotifyChannel = channel;

        // Смена критериев — перепривязываем курсор: уведомления только о новом после изменения.
        if (request.Query is { } query)
        {
            if (CatalogQueryBuilder.ValidateFilters(query.Cities, query.PriceFrom, query.PriceTo) is { } error)
                return Problem(title: error, statusCode: StatusCodes.Status400BadRequest);

            search.QueryJson = SavedSearchJson.Serialize(query);
            search.LastNotifiedListingId =
                await SavedSearchQueryPlanner.AnchorAsync(db.Listings.AsNoTracking(), query, ct);
        }

        // Реактивация (был выключен → включаем): проверяем лимит и перепривязываем курсор,
        // чтобы не завалить накопленным за время простоя.
        if (request.IsActive is { } isActive && isActive != search.IsActive)
        {
            if (isActive)
            {
                var active = await db.SavedSearches
                    .CountAsync(s => s.UserId == userId && s.IsActive && s.Id != search.Id, ct);
                if (active >= _o.MaxActivePerUser)
                    return Problem(
                        title: $"Достигнут лимит активных сохранённых поисков ({_o.MaxActivePerUser})",
                        statusCode: StatusCodes.Status409Conflict);

                var current = SavedSearchJson.TryDeserialize(search.QueryJson, out var q) ? q : new SavedSearchQuery();
                search.LastNotifiedListingId =
                    await SavedSearchQueryPlanner.AnchorAsync(db.Listings.AsNoTracking(), current, ct);
            }
            search.IsActive = isActive;
        }

        search.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Ok(ToResponse(search));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;

        var search = await db.SavedSearches.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
        if (search is null)
            return Problem(title: "Сохранённый поиск не найден", statusCode: StatusCodes.Status404NotFound);

        db.SavedSearches.Remove(search);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static SavedSearchResponse ToResponse(SavedSearch s)
    {
        var query = SavedSearchJson.TryDeserialize(s.QueryJson, out var q) ? q : new SavedSearchQuery();
        return new SavedSearchResponse(
            s.Id, s.Name, query, s.NotifyChannel, s.IsActive, s.NotifiedAt, s.CreatedAt);
    }
}
