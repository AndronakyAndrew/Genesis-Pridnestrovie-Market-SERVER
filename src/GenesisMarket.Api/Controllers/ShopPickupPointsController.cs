using System.Data;
using System.Text.Json;
using GenesisMarket.Api.Business;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Listings;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Controllers;

[Authorize]
[Route("api/shop/pickup-points")]
public sealed class ShopPickupPointsController(
    AppDbContext db,
    ICatalogReferences references,
    TimeProvider clock) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var ownerId = CurrentUserId()!.Value;
        if (!await IsBusinessAsync(ownerId, ct))
            return BusinessRequired();

        var profile = await db.BusinessProfiles.AsNoTracking()
            .Where(x => x.UserId == ownerId)
            .Select(x => new { x.ConditionsNote })
            .SingleAsync(ct);
        var points = await db.ShopPickupPoints.AsNoTracking()
            .Where(x => x.OwnerId == ownerId)
            .OrderByDescending(x => x.IsMain)
            .ThenBy(x => x.SortOrder)
            .ThenBy(x => x.CreatedAt)
            .ToListAsync(ct);

        return Ok(new ShopPickupPointsResponse(points.Select(ToDto).ToList(), profile.ConditionsNote));
    }

    [HttpPut]
    public async Task<IActionResult> Replace(ShopPickupPointsRequest request, CancellationToken ct)
    {
        var ownerId = CurrentUserId()!.Value;
        if (!await IsBusinessAsync(ownerId, ct))
            return BusinessRequired();

        if (request.Points is null)
            return Error(StatusCodes.Status400BadRequest, "validation_failed", "Укажите массив points.");
        if (request.Points.Count > 5)
            return Error(StatusCodes.Status400BadRequest, "pickup_points_limit", "Можно сохранить не более пяти точек.");
        if (request.Points.Select(x => x.Id).Where(x => x is not null).Distinct().Count() != request.Points.Count(x => x.Id is not null))
            return Error(StatusCodes.Status400BadRequest, "duplicate_pickup_point_id", "ID точки повторяется.");
        if (request.Points.Count > 0 && request.Points.Count(x => x.IsMain) != 1)
            return Error(StatusCodes.Status400BadRequest, "pickup_main_required", "Для списка точек выберите ровно одну основную.");
        if (request.Points.Any(x => x.IsMain && !x.IsActive))
            return Error(StatusCodes.Status400BadRequest, "pickup_main_inactive", "Основная точка должна быть активной.");

        var conditionsNote = ReadConditionsNote(request.ConditionsNote, out var conditionsError);
        if (conditionsError is not null)
            return Error(StatusCodes.Status400BadRequest, conditionsError,
                conditionsError == "conditions_note_too_long" ? "Условия получения ограничены 500 символами." : "conditionsNote должен быть строкой или null.");

        var citySnapshot = await references.GetAsync(ct);
        var normalized = new List<NormalizedPoint>(request.Points.Count);
        foreach (var input in request.Points)
        {
            var city = citySnapshot.City(input.City)?.Code;
            if (city is null)
                return Error(StatusCodes.Status400BadRequest, "invalid_city", "Выберите город из справочника проекта.");

            var address = BusinessInput.NormalizeText(input.Address);
            if (address.Length == 0)
                return Error(StatusCodes.Status400BadRequest, "pickup_address_required", "Укажите адрес точки.");
            if (address.Length > 200)
                return Error(StatusCodes.Status400BadRequest, "pickup_address_too_long", "Адрес ограничен 200 символами.");

            var note = string.IsNullOrWhiteSpace(input.Note) ? null : BusinessInput.NormalizeText(input.Note);
            if (note?.Length > 200)
                return Error(StatusCodes.Status400BadRequest, "pickup_note_too_long", "Примечание ограничено 200 символами.");

            if (!PickupSchedule.TryNormalize(input.Hours, out var hours, out var hoursError))
                return Error(StatusCodes.Status400BadRequest, hoursError!,
                    hoursError == "invalid_pickup_break" ? "Перерыв должен быть задан парой и находиться внутри часов работы каждого открытого дня." : "Укажите семь уникальных дней и корректные часы работы.");

            normalized.Add(new NormalizedPoint(input.Id, city, address, note, input.IsMain, input.IsActive, hours));
        }

        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            // Snapshot перед блокировкой позволяет распознать два параллельных PUT;
            // после блокировки reread не увидит версию, если другая запись уже успела завершиться.
            var beforeLock = await db.BusinessProfiles.AsNoTracking()
                .Where(x => x.UserId == ownerId)
                .Select(x => x.UpdatedAt)
                .SingleAsync(ct);
            var lockedVersion = await db.BusinessProfiles.FromSqlInterpolated(
                    $"SELECT * FROM business_profiles WHERE \"UserId\" = {ownerId} FOR UPDATE")
                .AsNoTracking()
                .Select(x => x.UpdatedAt)
                .SingleAsync(ct);
            if (beforeLock != lockedVersion)
                return await ConflictAsync(tx, ct);

            var business = await db.BusinessProfiles.SingleAsync(x => x.UserId == ownerId, ct);
            if (business.AccountType != GenesisMarket.Domain.Enums.AccountType.Business)
            {
                await tx.RollbackAsync(ct);
                return BusinessRequired();
            }
            var existing = await db.ShopPickupPoints.Where(x => x.OwnerId == ownerId).ToListAsync(ct);
            var existingById = existing.ToDictionary(x => x.Id);
            if (normalized.Any(x => x.Id is { } id && !existingById.ContainsKey(id)))
                return await NotFoundAsync(tx, ct);

            for (var index = 0; index < normalized.Count; index++)
            {
                var input = normalized[index];
                var hoursJson = input.Hours is null ? null : JsonSerializer.Serialize(input.Hours);
                if (input.Id is { } id)
                {
                    var point = existingById[id];
                    point.City = input.City;
                    point.Address = input.Address;
                    point.Note = input.Note;
                    point.IsMain = input.IsMain;
                    point.IsActive = input.IsActive;
                    point.SortOrder = index;
                    point.Hours = hoursJson;
                    point.UpdatedAt = NextTimestamp(now, point.UpdatedAt);
                }
                else
                {
                    db.ShopPickupPoints.Add(new ShopPickupPoint
                    {
                        OwnerId = ownerId,
                        City = input.City,
                        Address = input.Address,
                        Note = input.Note,
                        IsMain = input.IsMain,
                        IsActive = input.IsActive,
                        SortOrder = index,
                        Hours = hoursJson,
                        CreatedAt = now
                    });
                }
            }

            var requestedIds = normalized.Where(x => x.Id is not null).Select(x => x.Id!.Value).ToHashSet();
            db.ShopPickupPoints.RemoveRange(existing.Where(x => !requestedIds.Contains(x.Id)));
            if (request.ConditionsNote.ValueKind != JsonValueKind.Undefined)
                business.UpdateConditionsNote(conditionsNote, NextTimestamp(now, business.UpdatedAt));
            else
                business.UpdatePublicLinks(business.Instagram, business.Website, NextTimestamp(now, business.UpdatedAt));

            // Touch the owning row on every replace so the owner-scoped version catches
            // concurrent list replacements even when conditionsNote was omitted.
            if (business.UpdatedAt == beforeLock)
                business.UpdatePublicLinks(business.Instagram, business.Website, NextTimestamp(now, business.UpdatedAt));

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            var saved = await db.ShopPickupPoints.AsNoTracking()
                .Where(x => x.OwnerId == ownerId)
                .OrderByDescending(x => x.IsMain).ThenBy(x => x.SortOrder).ThenBy(x => x.CreatedAt)
                .ToListAsync(ct);
            return Ok(new ShopPickupPointsResponse(saved.Select(ToDto).ToList(), business.ConditionsNote));
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ConflictAsync(tx, ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return await ConflictAsync(tx, ct);
        }
    }

    private ShopPickupPointDto ToDto(ShopPickupPoint point)
    {
        var hours = point.Hours is null ? null : JsonSerializer.Deserialize<PickupHoursDto>(point.Hours);
        var state = PickupSchedule.Evaluate(hours, clock.GetUtcNow());
        return new ShopPickupPointDto(point.Id, point.City, point.Address, point.Note,
            point.IsMain, point.IsActive, hours, state.OpenNow, state.ClosesAt, state.OpensAt);
    }

    private async Task<bool> IsBusinessAsync(Guid ownerId, CancellationToken ct) =>
        await db.BusinessProfiles.AnyAsync(x => x.UserId == ownerId && x.AccountType == GenesisMarket.Domain.Enums.AccountType.Business, ct);

    private IActionResult BusinessRequired() => Error(StatusCodes.Status403Forbidden,
        "business_account_required", "Эта функция доступна только бизнес-аккаунту.");

    private ObjectResult Error(int status, string code, string title)
    {
        var problem = new ProblemDetails { Status = status, Title = title };
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status };
    }

    private async Task<ObjectResult> ConflictAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, CancellationToken ct)
    {
        await tx.RollbackAsync(ct);
        return Error(StatusCodes.Status409Conflict, "concurrent_change", "Список точек изменился параллельно. Обновите его и повторите.");
    }

    private async Task<ObjectResult> NotFoundAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, CancellationToken ct)
    {
        await tx.RollbackAsync(ct);
        return Error(StatusCodes.Status404NotFound, "not_found", "Точка самовывоза не найдена.");
    }

    private static string? ReadConditionsNote(JsonElement value, out string? error)
    {
        error = null;
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
        {
            error = "invalid_conditions_note";
            return null;
        }

        var note = BusinessInput.NormalizeText(value.GetString());
        if (note.Length > 500)
        {
            error = "conditions_note_too_long";
            return null;
        }
        return note.Length == 0 ? null : note;
    }

    private static DateTimeOffset NextTimestamp(DateTimeOffset now, DateTimeOffset? previous) =>
        previous is { } old && now <= old ? old.AddMilliseconds(1) : now;

    private sealed record NormalizedPoint(
        Guid? Id,
        string City,
        string Address,
        string? Note,
        bool IsMain,
        bool IsActive,
        PickupHoursDto? Hours);
}
