using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Profiles;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Controllers;

[Authorize]
[Route("api/shop/links")]
public sealed class ShopLinksController(
    AppDbContext db,
    TimeProvider clock) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var ownerId = CurrentUserId()!.Value;
        var profile = await db.BusinessProfiles.AsNoTracking()
            .Where(x => x.UserId == ownerId && x.AccountType == AccountType.Business)
            .Select(x => new ShopLinksDto(x.Instagram, x.Website))
            .SingleOrDefaultAsync(ct);
        return profile is null ? BusinessRequired() : Ok(profile);
    }

    [HttpPut]
    public async Task<IActionResult> Replace(ShopLinksDto request, CancellationToken ct)
    {
        var ownerId = CurrentUserId()!.Value;
        var profile = await db.BusinessProfiles
            .SingleOrDefaultAsync(x => x.UserId == ownerId && x.AccountType == AccountType.Business, ct);
        if (profile is null)
            return BusinessRequired();

        if (!ShopLinkNormalizer.TryInstagram(request.Instagram, out var instagram))
            return Error(StatusCodes.Status400BadRequest, "invalid_instagram",
                "Укажите Instagram username или ссылку instagram.com/username.");
        if (!ShopLinkNormalizer.TryWebsite(request.Website, out var website))
            return Error(StatusCodes.Status400BadRequest, "invalid_website",
                "Сайт должен использовать http или https и содержать не более 200 символов.");

        profile.UpdatePublicLinks(instagram, website, clock.GetUtcNow());
        try
        {
            await db.SaveChangesAsync(ct);
            return Ok(new ShopLinksDto(profile.Instagram, profile.Website));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error(StatusCodes.Status409Conflict, "concurrent_change",
                "Данные магазина изменились параллельно. Обновите страницу и повторите.");
        }
    }

    private IActionResult BusinessRequired() => Error(StatusCodes.Status403Forbidden,
        "business_account_required", "Эта функция доступна только бизнес-аккаунту.");

    private ObjectResult Error(int status, string code, string title)
    {
        var problem = new ProblemDetails { Status = status, Title = title };
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status };
    }
}
