using GenesisMarket.Api.Contracts;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Публичные профили продавцов. Ни email, ни телефона, ни точной даты
/// регистрации, ни Role, ни IsBanned наружу не отдаём.
/// </summary>
[Route("api/users")]
public class UsersController(AppDbContext db) : ApiControllerBase
{
    [AllowAnonymous]
    [HttpGet("{id:guid}/public")]
    public async Task<ActionResult<PublicProfileResponse>> GetPublic(Guid id, CancellationToken ct)
    {
        var user = await db.Users
            .AsNoTracking()
            .Include(u => u.Profile)
            .FirstOrDefaultAsync(u => u.Id == id, ct);

        if (user is null || user.IsDeleted || user.Profile is null)
            return Problem(title: "Профиль не найден", statusCode: StatusCodes.Status404NotFound);

        var activeListings = await db.Listings
            .CountAsync(l => l.OwnerId == id && l.Status == ListingStatus.Active, ct);

        // Дата регистрации — только месяц и год (первое число месяца).
        var registeredAt = new DateOnly(user.CreatedAt.Year, user.CreatedAt.Month, 1);

        return Ok(new PublicProfileResponse(
            user.Profile.DisplayName,
            user.Profile.City,
            user.Profile.AvatarUrl,
            registeredAt,
            activeListings,
            // Денормализованный агрегат отзывов (поддерживается триггером reviews_rating_sync).
            AverageRating: user.AverageRating,
            ReviewsCount: user.ReviewsCount,
            user.PhoneVerified));
    }
}
