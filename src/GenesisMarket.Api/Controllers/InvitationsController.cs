using System.Security.Cryptography;
using GenesisMarket.Api.Audit;
using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Invitations;
using GenesisMarket.Api.Listings;
using GenesisMarket.Api.Security;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Infrastructure.Auth;
using GenesisMarket.Infrastructure.Persistence;
using GenesisMarket.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Страница приглашения для владельца магазина (по токену из ссылки). Все ручки по токену —
/// под лимитом <see cref="RateLimitPolicies.InvitationToken"/> на IP: подобрать 256-битный
/// токен нельзя, но и перебор форм/ретраи не должны нагружать БД.
///
/// Наружу по токену — только название, город и превью товаров. Внутренняя заметка, данные
/// сотрудника и сам факт существования других приглашений не раскрываются.
/// </summary>
[Route("api/invitations")]
public class InvitationsController(
    AppDbContext db,
    InvitationService invitations,
    InvitationPhotoUrls photoUrls,
    ICatalogReferences references,
    LoginCodeService loginCodes,
    IPasswordHasher hasher,
    IPublicCodeGenerator publicCodes,
    IAuditLog auditLog) : ApiControllerBase
{
    private const string LinkNotFound = "Ссылка недействительна: она отозвана, заменена новой или уже использована";
    private const string LinkExpired = "Срок действия ссылки истёк. Попросите сотрудника прислать новую.";

    private const int PreviewDrafts = 12;

    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.InvitationToken)]
    [HttpGet("{token}")]
    public async Task<ActionResult<InvitationPublicResponse>> Get(string token, CancellationToken ct)
    {
        NoStore();
        var (state, invitation) = await invitations.ResolveAsync(token, track: false, ct);
        if (LinkError(state) is { } error)
            return error;

        var drafts = await db.InvitationDrafts.AsNoTracking()
            .Where(d => d.InvitationId == invitation!.Id)
            .OrderBy(d => d.SortOrder).ThenBy(d => d.CreatedAt)
            .Select(d => new
            {
                d.Title,
                Photo = d.Photos.OrderBy(p => p.SortOrder).Select(p => (Guid?)p.Id).FirstOrDefault()
            })
            .ToListAsync(ct);

        var catalog = await references.GetAsync(ct);
        return Ok(new InvitationPublicResponse(
            invitation!.ShopName, invitation.City, catalog.CityName(invitation.City), invitation.ExpiresAt!.Value,
            drafts.Count,
            drafts.Take(PreviewDrafts)
                .Select(d => new InvitationPreviewDraft(d.Title,
                    d.Photo is { } id ? photoUrls.Build(Request, id, thumb: true) : null))
                .ToList()));
    }

    /// <summary>
    /// Регистрация по ссылке без пароля: для нового адреса создаётся аккаунт (имя продавца —
    /// название магазина, город — город приглашения), затем на почту уходит обычный код входа.
    /// Для уже зарегистрированного адреса — только код. Ответ одинаков в обоих случаях:
    /// ручка не должна быть оракулом «есть ли такой аккаунт». Дальше клиент входит через
    /// <c>/api/auth/login-code/verify</c> и вызывает <see cref="Accept"/>.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.InvitationToken)]
    [HttpPost("{token}/signup")]
    public async Task<IActionResult> Signup(string token, InvitationSignupRequest request, CancellationToken ct)
    {
        var (state, invitation) = await invitations.ResolveAsync(token, track: false, ct);
        if (LinkError(state) is { } error)
            return error;

        var email = request.Email.Trim().ToLowerInvariant();
        // Хеш считаем всегда — ответ на занятый адрес не должен отличаться по времени.
        var secretHash = hasher.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        if (!await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            var user = new User
            {
                Email = email,
                PasswordHash = secretHash,
                HasPassword = false,
                PublicCode = await publicCodes.NextAsync(ct),
                Profile = new Profile
                {
                    DisplayName = invitation!.ShopName,
                    City = invitation.City
                }
            };
            db.Users.Add(user);
            auditLog.Record(AuditActions.InvitationSignup, AuditActions.TargetInvitation, invitation.Id.ToString(),
                newValue: new { userId = user.Id }, actingUserId: user.Id);
            try
            {
                await NewAccounts.SaveAsync(db, publicCodes, user, ct);
            }
            catch (DbUpdateException)
            {
                // Параллельная регистрация того же адреса успела раньше — ведём себя как с занятым.
                db.ChangeTracker.Clear();
            }
        }

        await loginCodes.RequestAsync(email, ct);
        return Ok(new MessageResponse("Мы отправили код входа на указанную почту."));
    }

    /// <summary>
    /// Принять приглашение текущим аккаунтом. Одноразово: после успеха ссылка гаснет, из двух
    /// одновременных запросов проходит один (см. <see cref="InvitationService.AcceptAsync"/>).
    /// </summary>
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.InvitationToken)]
    [HttpPost("{token}/accept")]
    public async Task<ActionResult<InvitationAcceptResponse>> Accept(string token, CancellationToken ct)
    {
        var result = await invitations.AcceptAsync(token, CurrentUserId()!.Value, ct);
        return result.Outcome switch
        {
            AcceptOutcome.Accepted => Ok(new InvitationAcceptResponse(result.DraftsCount,
                "Приглашение принято. Черновики товаров — в вашем кабинете; без вашего подтверждения ничего не публикуется.")),
            AcceptOutcome.Expired => Problem(title: LinkExpired, statusCode: StatusCodes.Status410Gone),
            AcceptOutcome.OwnInvitation => Problem(title: "Нельзя принять приглашение, которое вы подготовили сами",
                statusCode: StatusCodes.Status403Forbidden),
            AcceptOutcome.Lost => Problem(title: LinkNotFound, statusCode: StatusCodes.Status409Conflict),
            _ => Problem(title: LinkNotFound, statusCode: StatusCodes.Status404NotFound)
        };
    }

    /// <summary>
    /// Фото черновика по подписанной ссылке (выдают мастер сотрудника и страница приглашения).
    /// Без действующей подписи — 404, как несуществующее. Кешируется только приватно и на срок подписи.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("~/api/invitation-photos/{id:guid}")]
    public async Task<IActionResult> Photo(
        Guid id, [FromQuery] string? v, [FromQuery] long exp, [FromQuery] string? sig,
        [FromServices] IObjectStorage storage, CancellationToken ct)
    {
        var thumb = v == "t";
        if (v is not ("t" or "o") || !photoUrls.Verify(id, thumb, exp, sig))
            return Problem(title: "Изображение не найдено", statusCode: StatusCodes.Status404NotFound);

        var key = await db.InvitationDraftPhotos.AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => thumb ? p.ThumbKey : p.ObjectKey)
            .FirstOrDefaultAsync(ct);
        if (key is null)
            return Problem(title: "Изображение не найдено", statusCode: StatusCodes.Status404NotFound);

        try
        {
            var stream = await storage.GetAsync(key, ct);
            var ttl = Math.Max(0, exp - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Response.Headers.CacheControl = $"private, max-age={ttl}";
            Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            return File(stream, "image/webp");
        }
        catch (FileNotFoundException)
        {
            return Problem(title: "Изображение не найдено", statusCode: StatusCodes.Status404NotFound);
        }
    }

    // ---- helpers ----

    private ObjectResult? LinkError(InvitationLinkState state) => state switch
    {
        InvitationLinkState.Active => null,
        InvitationLinkState.Expired => Problem(title: LinkExpired, statusCode: StatusCodes.Status410Gone),
        _ => Problem(title: LinkNotFound, statusCode: StatusCodes.Status404NotFound)
    };

    /// <summary>Ответ по токену не кешируется нигде и не индексируется.</summary>
    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    }
}
