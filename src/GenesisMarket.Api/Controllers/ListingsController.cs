using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// CRUD объявлений. Соглашения проекта: наружу — только DTO;
/// создавать может только авторизованный пользователь с подтверждённым по SMS
/// телефоном; редактировать/удалять — только владелец.
/// (Полноценный JWT добавляется на шаге 2; здесь владелец берётся из claim.)
/// </summary>
public class ListingsController(
    AppDbContext db,
    IPublishingPolicy publishing,
    IAuthorizationService authorization) : ApiControllerBase
{
    // Каталог публичен — гости просматривают объявления без авторизации.
    [AllowAnonymous]
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ListingResponse>>> GetAll(CancellationToken ct)
    {
        // Глобальный query filter уже отсекает мягко удалённые объявления.
        var items = await db.Listings
            .AsNoTracking()
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => Map(l))
            .ToListAsync(ct);

        return Ok(items);
    }

    [AllowAnonymous]
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ListingResponse>> GetById(Guid id, CancellationToken ct)
    {
        var listing = await db.Listings.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == id, ct);

        if (listing is null)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);

        return Ok(Map(listing));
    }

    [Authorize]
    [HttpPost]
    public async Task<ActionResult<ListingResponse>> Create(
        CreateListingRequest request,
        CancellationToken ct)
    {
        // Только авторизованные пользователи могут подавать объявления.
        var userId = CurrentUserId();
        if (userId is null)
            return Problem(title: "Требуется авторизация", statusCode: StatusCodes.Status401Unauthorized);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId.Value, ct);
        if (user is null)
            return Problem(title: "Требуется авторизация", statusCode: StatusCodes.Status401Unauthorized);

        // Анти-фрод: публикация только после требуемого подтверждения контакта
        // (политика в конфиге Publishing: на проде — почта).
        var (canPublish, reason) = publishing.CanPublish(user);
        if (!canPublish)
            return Problem(title: reason, statusCode: StatusCodes.Status403Forbidden);

        // Подкатегория должна существовать и относиться к указанной категории.
        var subcategoryOk = await db.Subcategories.AnyAsync(
            s => s.Id == request.SubcategoryId && s.Category == request.Category, ct);
        if (!subcategoryOk)
            return Problem(
                title: "Подкатегория не найдена или не соответствует категории",
                statusCode: StatusCodes.Status400BadRequest);

        // Согласованность цены и типа цены (дублирует CHECK БД, но даёт понятную ошибку).
        if (!PriceMatchesType(request.PriceType, request.Price))
            return Problem(
                title: "Цена не соответствует выбранному типу цены",
                statusCode: StatusCodes.Status400BadRequest);

        var listing = new Listing
        {
            Title = request.Title,
            Description = request.Description,
            Price = request.Price,
            PriceType = request.PriceType,
            Category = request.Category,
            SubcategoryId = request.SubcategoryId,
            City = request.City,
            District = request.District,
            Condition = request.Condition,
            Status = ListingStatus.Draft,
            OwnerId = userId.Value
        };

        db.Listings.Add(listing);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetById), new { id = listing.Id }, Map(listing));
    }

    [Authorize]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var listing = await db.Listings.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (listing is null)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);

        // Проверка владельца — только через IAuthorizationService, не сравнением в контроллере.
        var result = await authorization.AuthorizeAsync(User, listing, ResourceOwnerRequirement.Policy);
        if (!result.Succeeded)
            // Существование объявления публично (каталог) — отдаём 403, не 404.
            return Forbid();

        // Мягкое удаление: строку физически не удаляем.
        listing.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    private static bool PriceMatchesType(PriceType type, decimal? price) => type switch
    {
        PriceType.Free => price == 0,
        PriceType.Negotiable => price is null,
        PriceType.Fixed => price is not null && price >= 0,
        _ => false
    };

    // Маппинг сущность → DTO. Сущность EF наружу не отдаём.
    private static ListingResponse Map(Listing l) => new(
        l.Id, l.Title, l.Description, l.Price, l.PriceType, l.Category,
        l.SubcategoryId, l.City, l.District, l.Condition, l.Status,
        l.ViewsCount, l.OwnerId, l.CreatedAt, l.PublishedAt);
}
