using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Moderation;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Чёрный список карт, на которые мошенники требуют предоплату. Номер принимается
/// один раз (в теле POST) и сразу превращается в HMAC + последние 4 цифры: ни в БД,
/// ни в ответах, ни в журнале его нет. Объявления с картой из списка уходят на
/// премодерацию (<see cref="ICardBlocklist"/>).
/// </summary>
[Route("api/moderation/blocked-cards")]
[Authorize(Policy = "Moderator")]
public class ModerationBlockedCardsController(
    AppDbContext db, ICardHasher hasher, IModerationAudit audit) : ApiControllerBase
{
    private const int DefaultLimit = 25;
    private const int MaxLimit = 100;

    [HttpGet]
    public async Task<ActionResult<BlockedCardsPage>> List(
        [FromQuery] string? last4, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var q = db.BlockedCards.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(last4))
            q = q.Where(c => c.Last4 == last4.Trim());

        if (cursor is { Length: > 0 })
        {
            if (!ModerationCursor.TryDecode(cursor, out _, out var at, out var afterId))
                return Problem(title: "Некорректный курсор", statusCode: StatusCodes.Status400BadRequest);
            q = q.Where(c => c.CreatedAt < at || (c.CreatedAt == at && c.Id < afterId));
        }

        var rows = await q.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .Take(take + 1).ToListAsync(ct);
        var hasMore = rows.Count > take;
        var page = rows.Take(take).ToList();
        var actors = await ModerationReadModels.LoadActorsAsync(db, page.Select(c => c.CreatedByUserId), ct);

        var items = page.Select(c => new BlockedCardItem(
            c.Id, c.Last4, c.Reason, c.SourceReportId,
            actors.GetValueOrDefault(c.CreatedByUserId) ?? ModerationReadModels.UnknownActor(c.CreatedByUserId),
            c.CreatedAt)).ToList();
        var next = hasMore ? ModerationCursor.Encode(0, page[^1].CreatedAt, page[^1].Id) : null;

        return Ok(new BlockedCardsPage(items, next, hasMore));
    }

    /// <summary>Внести карту. 409 — уже в списке (запись одна на номер).</summary>
    [HttpPost]
    public async Task<ActionResult<BlockedCardItem>> Block(BlockCardRequest request, CancellationToken ct)
    {
        var digits = CardNumbers.Normalize(request.CardNumber);
        if (digits is null)
            return Problem(
                title: $"Номер карты — от {CardNumbers.MinDigits} до {CardNumbers.MaxDigits} цифр",
                statusCode: StatusCodes.Status400BadRequest);

        // Без ключа хеширования номер пришлось бы хранить открыто — этого не делаем.
        var hash = hasher.Hash(digits);
        if (hash is null)
            return Problem(title: "Чёрный список карт недоступен: не задан ключ хеширования",
                statusCode: StatusCodes.Status503ServiceUnavailable);

        if (await db.BlockedCards.AnyAsync(c => c.CardHash == hash, ct))
            return Problem(title: "Карта уже в чёрном списке", statusCode: StatusCodes.Status409Conflict);

        var me = CurrentUserId()!.Value;
        var card = new BlockedCard
        {
            CardHash = hash,
            Last4 = digits[^4..],
            Reason = request.Reason.Trim(),
            SourceReportId = request.SourceReportId,
            CreatedByUserId = me
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.BlockedCards.Add(card);
        audit.Record(ModerationLog.ActionBlockCard, ModerationLog.TargetCard, card.Id,
            reason: card.Reason,
            payload: new { last4 = card.Last4, sourceReportId = card.SourceReportId });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var actor = (await ModerationReadModels.LoadActorsAsync(db, [me], ct)).GetValueOrDefault(me)
                    ?? ModerationReadModels.UnknownActor(me);
        return StatusCode(StatusCodes.Status201Created, new BlockedCardItem(
            card.Id, card.Last4, card.Reason, card.SourceReportId, actor, card.CreatedAt));
    }

    /// <summary>Убрать карту из списка (ошибочно внесённую).</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Unblock(Guid id, CancellationToken ct)
    {
        var card = await db.BlockedCards.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (card is null)
            return Problem(title: "Запись не найдена", statusCode: StatusCodes.Status404NotFound);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.BlockedCards.Remove(card);
        audit.Record(ModerationLog.ActionUnblockCard, ModerationLog.TargetCard, id,
            payload: new { last4 = card.Last4 });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return NoContent();
    }
}
