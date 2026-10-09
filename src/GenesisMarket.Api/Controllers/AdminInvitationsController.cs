using GenesisMarket.Api.Audit;
using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Invitations;
using GenesisMarket.Api.Listings;
using GenesisMarket.Api.Seo;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Invitations;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Приглашения продавцов — мастер сотрудника (<see cref="Permissions.InvitationsManage"/>:
/// Admin и Owner). Сотрудник видит и меняет только свои приглашения; владелец площадки
/// (<see cref="Permissions.InvitationsAll"/>) — все. Чужое для сотрудника — 404, как
/// несуществующее.
///
/// Любое изменение идёт под блокировкой строки приглашения (SELECT … FOR UPDATE): принятие
/// меняет ту же строку условным UPDATE, поэтому правка черновика, отзыв или перевыпуск
/// не могут проскочить между проверкой статуса и принятием. Каждое действие — в журнал.
/// </summary>
[Route("api/admin/invitations")]
[Authorize(Policy = Permissions.InvitationsManage)]
public class AdminInvitationsController(
    AppDbContext db,
    InvitationService invitations,
    InvitationPhotoUrls photoUrls,
    ICatalogReferences references,
    IAuditLog auditLog,
    IOptions<InvitationOptions> options,
    IOptions<ListingOptions> listingOptions,
    IOptions<SeoOptions> seo) : ApiControllerBase
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;

    private const string NotFoundTitle = "Приглашение не найдено";
    private const string NotEditableTitle = "Приглашение уже принято или отозвано — менять его нельзя";

    // ---- Список ----

    [HttpGet]
    public async Task<ActionResult<InvitationListPage>> List([FromQuery] InvitationListQuery query, CancellationToken ct)
    {
        var me = CurrentUserId()!.Value;
        var now = DateTimeOffset.UtcNow;
        var limit = Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit);

        var source = Visible(db.SellerInvitations.AsNoTracking());
        if (query.Mine == true)
            source = source.Where(i => i.CreatedByUserId == me);

        // «Истекло» в фильтре — и зафиксированное джобом, и досчитанное: срок вышел, джоб ещё не прошёл.
        source = query.Status switch
        {
            InvitationStatus.Expired => source.Where(i => i.Status == InvitationStatus.Expired
                || (i.Status == InvitationStatus.Pending && i.ExpiresAt <= now)),
            InvitationStatus.Pending => source.Where(i => i.Status == InvitationStatus.Pending && i.ExpiresAt > now),
            { } s => source.Where(i => i.Status == s),
            null => source
        };

        // Курсор — Id последней строки: UUID v7 упорядочен по времени создания.
        if (!string.IsNullOrEmpty(query.Cursor))
        {
            if (!Guid.TryParse(query.Cursor, out var after))
                return Problem(title: "Некорректный курсор", statusCode: StatusCodes.Status400BadRequest);
            source = source.Where(i => i.Id.CompareTo(after) < 0);
        }

        var rows = await source
            .OrderByDescending(i => i.Id)
            .Take(limit + 1)
            .Select(i => new
            {
                i.Id, i.ShopName, i.City, i.Status, i.ExpiresAt, i.CreatedByUserId, i.CreatedAt,
                Drafts = i.Drafts.Count,
                Published = db.Listings.Count(l => l.SourceInvitationId == i.Id && l.Status != ListingStatus.Draft)
            })
            .ToListAsync(ct);

        var page = rows.Take(limit).ToList();
        var creatorIds = page.Select(r => r.CreatedByUserId).Distinct().ToList();
        var creators = await db.Users.AsNoTracking()
            .Where(u => creatorIds.Contains(u.Id))
            .Select(u => new { u.Id, u.PublicCode, Name = u.Profile != null ? u.Profile.DisplayName : null })
            .ToDictionaryAsync(u => u.Id, ct);

        var items = page.ConvertAll(r => new InvitationListItem(
            r.Id, r.ShopName, r.City, Effective(r.Status, r.ExpiresAt, now), r.ExpiresAt, r.CreatedByUserId,
            creators.GetValueOrDefault(r.CreatedByUserId)?.PublicCode,
            creators.GetValueOrDefault(r.CreatedByUserId)?.Name,
            r.Drafts, r.Published, r.CreatedAt));

        return Ok(new InvitationListPage(items, rows.Count > limit ? page[^1].Id.ToString() : null));
    }

    // ---- Шаг 1: магазин ----

    [HttpPost]
    public async Task<ActionResult<InvitationDetailResponse>> Create(InvitationShopRequest request, CancellationToken ct)
    {
        var me = CurrentUserId()!.Value;
        if (await ShopCodesAsync(request, null, ct) is not { } codes)
            return Problem(title: "Неизвестный или выключенный город либо категория",
                statusCode: StatusCodes.Status400BadRequest);

        var invitation = new SellerInvitation
        {
            ShopName = request.ShopName.Trim(),
            City = codes.City,
            Category = codes.Category,
            InternalNote = NormalizeNote(request.InternalNote),
            CreatedByUserId = me
        };
        db.SellerInvitations.Add(invitation);
        auditLog.Record(AuditActions.InvitationCreate, AuditActions.TargetInvitation, invitation.Id.ToString(),
            newValue: ShopSnapshot(invitation));
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = invitation.Id }, await DetailAsync(invitation.Id, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<InvitationDetailResponse>> Get(Guid id, CancellationToken ct)
    {
        if (!await Visible(db.SellerInvitations).AnyAsync(i => i.Id == id, ct))
            return Problem(title: NotFoundTitle, statusCode: StatusCodes.Status404NotFound);
        return Ok(await DetailAsync(id, ct));
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<InvitationDetailResponse>> UpdateShop(
        Guid id, InvitationShopRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await LockAsync(id, ct) is not { } invitation)
            return Problem(title: NotFoundTitle, statusCode: StatusCodes.Status404NotFound);
        if (!invitation.IsEditable)
            return Problem(title: NotEditableTitle, statusCode: StatusCodes.Status409Conflict);

        if (await ShopCodesAsync(request, invitation, ct) is not { } codes)
            return Problem(title: "Неизвестный или выключенный город либо категория",
                statusCode: StatusCodes.Status400BadRequest);

        var before = ShopSnapshot(invitation);
        invitation.ShopName = request.ShopName.Trim();
        invitation.City = codes.City;
        invitation.Category = codes.Category;
        invitation.InternalNote = NormalizeNote(request.InternalNote);
        invitation.UpdatedAt = DateTimeOffset.UtcNow;

        auditLog.Record(AuditActions.InvitationUpdate, AuditActions.TargetInvitation, id.ToString(),
            oldValue: before, newValue: ShopSnapshot(invitation));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Ok(await DetailAsync(id, ct));
    }

    // ---- Шаг 2: товары ----

    [HttpPost("{id:guid}/drafts")]
    public async Task<ActionResult<InvitationDraftResponse>> CreateDraft(
        Guid id, InvitationDraftRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await LockAsync(id, ct) is not { } invitation)
            return Problem(title: NotFoundTitle, statusCode: StatusCodes.Status404NotFound);
        if (!invitation.IsEditable)
            return Problem(title: NotEditableTitle, statusCode: StatusCodes.Status409Conflict);

        var count = await db.InvitationDrafts.CountAsync(d => d.InvitationId == id, ct);
        if (count >= options.Value.MaxDraftsPerInvitation)
            return Problem(title: $"Не больше {options.Value.MaxDraftsPerInvitation} товаров в одном приглашении",
                statusCode: StatusCodes.Status409Conflict);

        var (error, category) = await ValidateDraftAsync(request, null, ct);
        if (error is not null)
            return error;

        var draft = new InvitationDraft
        {
            InvitationId = id,
            Title = "",
            Description = "",
            Category = category,
            SortOrder = count
        };
        Apply(draft, request, category);
        db.InvitationDrafts.Add(draft);
        invitation.UpdatedAt = DateTimeOffset.UtcNow;

        auditLog.Record(AuditActions.InvitationDraftCreate, AuditActions.TargetInvitation, id.ToString(),
            newValue: DraftSnapshot(draft));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Ok(MapDraft(draft));
    }

    [HttpPut("{id:guid}/drafts/{draftId:guid}")]
    public async Task<ActionResult<InvitationDraftResponse>> UpdateDraft(
        Guid id, Guid draftId, InvitationDraftRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await LockAsync(id, ct) is not { } invitation)
            return Problem(title: NotFoundTitle, statusCode: StatusCodes.Status404NotFound);
        if (!invitation.IsEditable)
            return Problem(title: NotEditableTitle, statusCode: StatusCodes.Status409Conflict);

        var draft = await db.InvitationDrafts.Include(d => d.Photos)
            .FirstOrDefaultAsync(d => d.Id == draftId && d.InvitationId == id, ct);
        if (draft is null)
            return Problem(title: "Товар не найден", statusCode: StatusCodes.Status404NotFound);

        var (error, category) = await ValidateDraftAsync(request, draft, ct);
        if (error is not null)
            return error;

        var before = DraftSnapshot(draft);
        Apply(draft, request, category);
        invitation.UpdatedAt = draft.UpdatedAt;

        auditLog.Record(AuditActions.InvitationDraftUpdate, AuditActions.TargetInvitation, id.ToString(),
            oldValue: before, newValue: DraftSnapshot(draft));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Ok(MapDraft(draft));
    }

    [HttpDelete("{id:guid}/drafts/{draftId:guid}")]
    public async Task<IActionResult> DeleteDraft(Guid id, Guid draftId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await LockAsync(id, ct) is not { } invitation)
            return Problem(title: NotFoundTitle, statusCode: StatusCodes.Status404NotFound);
        if (!invitation.IsEditable)
            return Problem(title: NotEditableTitle, statusCode: StatusCodes.Status409Conflict);

        var draft = await db.InvitationDrafts.Include(d => d.Photos)
            .FirstOrDefaultAsync(d => d.Id == draftId && d.InvitationId == id, ct);
        if (draft is null)
            return Problem(title: "Товар не найден", statusCode: StatusCodes.Status404NotFound);

        // Ссылку уже выдали, а товаров не осталось — владелец принял бы пустое приглашение.
        if (invitation.Status is InvitationStatus.Pending or InvitationStatus.Expired &&
            await db.InvitationDrafts.CountAsync(d => d.InvitationId == id, ct) <= 1)
            return Problem(title: "В приглашении со ссылкой должен остаться хотя бы один товар",
                statusCode: StatusCodes.Status409Conflict);

        invitations.EnqueuePhotoDeletion(draft.Photos);
        db.InvitationDrafts.Remove(draft);
        invitation.UpdatedAt = DateTimeOffset.UtcNow;

        auditLog.Record(AuditActions.InvitationDraftDelete, AuditActions.TargetInvitation, id.ToString(),
            oldValue: DraftSnapshot(draft));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return NoContent();
    }

    [HttpPost("{id:guid}/drafts/{draftId:guid}/photos")]
    [RequestSizeLimit(InvitationService.MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = InvitationService.MaxRequestBytes)]
    public async Task<ActionResult<InvitationPhotoResponse>> UploadPhoto(
        Guid id, Guid draftId, IFormFile? file, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await LockAsync(id, ct) is not { } invitation)
            return Problem(title: NotFoundTitle, statusCode: StatusCodes.Status404NotFound);
        if (!invitation.IsEditable)
            return Problem(title: NotEditableTitle, statusCode: StatusCodes.Status409Conflict);

        var draft = await db.InvitationDrafts.FirstOrDefaultAsync(d => d.Id == draftId && d.InvitationId == id, ct);
        if (draft is null)
            return Problem(title: "Товар не найден", statusCode: StatusCodes.Status404NotFound);

        var (error, photo) = await invitations.UploadPhotoAsync(draft, file, ct);
        switch (error)
        {
            case PhotoUploadError.Empty:
                return Problem(title: "Файл изображения обязателен", statusCode: StatusCodes.Status400BadRequest);
            case PhotoUploadError.TooLarge:
                return Problem(title: "Файл больше 10 МБ", statusCode: StatusCodes.Status413PayloadTooLarge);
            case PhotoUploadError.LimitReached:
                return Problem(title: $"Не больше {options.Value.MaxPhotosPerDraft} фото на товар",
                    statusCode: StatusCodes.Status409Conflict);
            case PhotoUploadError.Unsupported:
                return Problem(title: "Поддерживаются изображения JPEG, PNG, WebP и HEIC/HEIF",
                    statusCode: StatusCodes.Status415UnsupportedMediaType);
            case PhotoUploadError.TooManyPixels:
                return Problem(title: "Изображение слишком большое", statusCode: StatusCodes.Status400BadRequest);
        }

        draft.UpdatedAt = invitation.UpdatedAt = DateTimeOffset.UtcNow;
        draft.UpdatedByUserId = CurrentUserId()!.Value;
        auditLog.Record(AuditActions.InvitationPhotoAdd, AuditActions.TargetInvitation, id.ToString(),
            newValue: new { draftId, photoId = photo!.Id });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Ok(MapPhoto(photo));
    }

    [HttpDelete("{id:guid}/drafts/{draftId:guid}/photos/{photoId:guid}")]
    public async Task<IActionResult> DeletePhoto(Guid id, Guid draftId, Guid photoId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await LockAsync(id, ct) is not { } invitation)
            return Problem(title: NotFoundTitle, statusCode: StatusCodes.Status404NotFound);
        if (!invitation.IsEditable)
            return Problem(title: NotEditableTitle, statusCode: StatusCodes.Status409Conflict);

        var photo = await db.InvitationDraftPhotos
            .FirstOrDefaultAsync(p => p.Id == photoId && p.DraftId == draftId && p.Draft!.InvitationId == id, ct);
        if (photo is null)
            return Problem(title: "Фото не найдено", statusCode: StatusCodes.Status404NotFound);

        db.InvitationDraftPhotos.Remove(photo);
        invitations.EnqueuePhotoDeletion([photo]);
        invitation.UpdatedAt = DateTimeOffset.UtcNow;
        auditLog.Record(AuditActions.InvitationPhotoDelete, AuditActions.TargetInvitation, id.ToString(),
            oldValue: new { draftId, photoId });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return NoContent();
    }

    // ---- Шаг 3: ссылка ----

    /// <summary>Первая ссылка: «Готовится» → «Ожидает». Нужен хотя бы один товар.</summary>
    [HttpPost("{id:guid}/link")]
    public Task<ActionResult<InvitationLinkResponse>> CreateLink(Guid id, CancellationToken ct) =>
        IssueAsync(id, reissue: false, ct);

    /// <summary>Новая ссылка взамен прежней («Ожидает» или «Истекло»): старая перестаёт работать сразу.</summary>
    [HttpPost("{id:guid}/reissue")]
    public Task<ActionResult<InvitationLinkResponse>> Reissue(Guid id, CancellationToken ct) =>
        IssueAsync(id, reissue: true, ct);

    [HttpPost("{id:guid}/revoke")]
    public async Task<ActionResult<InvitationDetailResponse>> Revoke(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await LockAsync(id, ct) is not { } invitation)
            return Problem(title: NotFoundTitle, statusCode: StatusCodes.Status404NotFound);
        if (!invitation.IsEditable)
            return Problem(title: NotEditableTitle, statusCode: StatusCodes.Status409Conflict);

        var before = invitation.Status;
        invitation.Revoke(DateTimeOffset.UtcNow);
        auditLog.Record(AuditActions.InvitationRevoke, AuditActions.TargetInvitation, id.ToString(),
            oldValue: new { status = before.ToString() },
            newValue: new { status = invitation.Status.ToString() });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Ok(await DetailAsync(id, ct));
    }

    // ---- helpers ----

    private async Task<ActionResult<InvitationLinkResponse>> IssueAsync(Guid id, bool reissue, CancellationToken ct)
    {
        if (seo.Value.NormalizedBaseUrl is not { } site)
            return Problem(title: "Не задан публичный адрес сайта (Seo:WebBaseUrl)",
                statusCode: StatusCodes.Status503ServiceUnavailable);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await LockAsync(id, ct) is not { } invitation)
            return Problem(title: NotFoundTitle, statusCode: StatusCodes.Status404NotFound);

        var allowed = reissue
            ? invitation.Status is InvitationStatus.Pending or InvitationStatus.Expired
            : invitation.Status == InvitationStatus.Draft;
        if (!allowed)
            return Problem(
                title: reissue
                    ? "Перевыпустить можно только ожидающую или истёкшую ссылку"
                    : "Ссылка уже создана — используйте перевыпуск",
                statusCode: StatusCodes.Status409Conflict);

        if (!await db.InvitationDrafts.AnyAsync(d => d.InvitationId == id, ct))
            return Problem(title: "Добавьте хотя бы один товар перед созданием ссылки",
                statusCode: StatusCodes.Status409Conflict);

        var before = new { status = invitation.Status.ToString(), invitation.LinkVersion };
        var (raw, hash) = InvitationTokens.Generate();
        invitation.IssueLink(hash, DateTimeOffset.UtcNow, invitations.LinkLifetime);

        auditLog.Record(reissue ? AuditActions.InvitationLinkReissue : AuditActions.InvitationLinkIssue,
            AuditActions.TargetInvitation, id.ToString(),
            oldValue: before,
            newValue: new { status = invitation.Status.ToString(), invitation.LinkVersion, invitation.ExpiresAt });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Ok(new InvitationLinkResponse($"{site}/invite/{raw}", invitation.ExpiresAt!.Value, invitation.LinkVersion));
    }

    /// <summary>Приглашения, доступные текущему сотруднику: свои либо все (владелец площадки).</summary>
    private IQueryable<SellerInvitation> Visible(IQueryable<SellerInvitation> source)
    {
        if (CurrentUser.Can(Permissions.InvitationsAll))
            return source;
        var me = CurrentUserId()!.Value;
        return source.Where(i => i.CreatedByUserId == me);
    }

    /// <summary>Строка приглашения под блокировкой до конца транзакции; чужое — null.</summary>
    private async Task<SellerInvitation?> LockAsync(Guid id, CancellationToken ct)
    {
        var invitation = (await db.SellerInvitations
            .FromSql($"SELECT * FROM seller_invitations WHERE \"Id\" = {id} FOR UPDATE")
            .ToListAsync(ct)).FirstOrDefault();

        if (invitation is null)
            return null;
        if (invitation.CreatedByUserId != CurrentUserId() && !CurrentUser.Can(Permissions.InvitationsAll))
            return null;

        // Срок вышел, а джоб ещё не прошёл: фиксируем «Истекло» здесь же, под той же блокировкой.
        if (invitation.Status == InvitationStatus.Pending && !invitation.IsLinkActive(DateTimeOffset.UtcNow))
            invitation.Expire(DateTimeOffset.UtcNow);

        return invitation;
    }

    private async Task<(string City, string Category)?> ShopCodesAsync(
        InvitationShopRequest request, SellerInvitation? existing, CancellationToken ct)
    {
        var catalog = await references.GetAsync(ct);
        var city = catalog.WritableCity(request.City, existing?.City);
        var category = catalog.WritableCategory(request.Category, existing?.Category);
        return city is null || category is null ? null : (city, category);
    }

    /// <summary>
    /// Черновик проверяется теми же правилами, что объявление, — после принятия он становится
    /// объявлением без дополнительного ввода. Заодно приводит категорию к каноническому коду.
    /// </summary>
    private async Task<(ObjectResult? Error, string Category)> ValidateDraftAsync(
        InvitationDraftRequest request, InvitationDraft? existing, CancellationToken ct)
    {
        var title = request.Title?.Trim() ?? "";
        var description = request.Description?.Trim() ?? "";
        string? error = null;
        if (title.Length is < 10 or > 100)
            error = "Название — от 10 до 100 символов";
        else if (description.Length is < 20 or > 5000)
            error = "Описание — от 20 до 5000 символов";
        else if (!Enum.IsDefined(request.PriceType) || !Enum.IsDefined(request.Condition))
            error = "Некорректный тип цены или состояние";
        else if (!ListingRules.PriceMatchesType(request.PriceType, request.Price))
            error = "Цена не соответствует выбранному типу цены";
        else if (request.Price is { } p && (p < 0 || p > listingOptions.Value.MaxPrice))
            error = "Недопустимая цена";
        if (error is not null)
            return (Problem(title: error, statusCode: StatusCodes.Status400BadRequest), "");

        var catalog = await references.GetAsync(ct);
        if (catalog.WritableCategory(request.Category, existing?.Category) is not { } category)
            return (Problem(title: "Неизвестная или выключенная категория",
                statusCode: StatusCodes.Status400BadRequest), "");

        int? current = existing?.SubcategoryId;
        var subOk = await db.Subcategories.AnyAsync(s => s.Id == request.SubcategoryId && s.Category == category
            && (s.IsActive || s.Id == current), ct);
        if (!subOk)
            return (Problem(title: "Подкатегория не найдена или не соответствует категории",
                statusCode: StatusCodes.Status400BadRequest), "");

        return (null, category);
    }

    private void Apply(InvitationDraft draft, InvitationDraftRequest request, string category)
    {
        draft.Title = request.Title.Trim();
        draft.Description = request.Description.Trim();
        draft.PriceType = request.PriceType;
        draft.Price = request.Price;
        draft.Category = category;
        draft.SubcategoryId = request.SubcategoryId;
        draft.Condition = request.Condition;
        draft.UpdatedByUserId = CurrentUserId()!.Value;
        draft.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string? NormalizeNote(string? note) =>
        string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    private static InvitationStatus Effective(InvitationStatus status, DateTimeOffset? expiresAt, DateTimeOffset now) =>
        status == InvitationStatus.Pending && expiresAt <= now ? InvitationStatus.Expired : status;

    private async Task<InvitationDetailResponse> DetailAsync(Guid id, CancellationToken ct)
    {
        var i = await db.SellerInvitations.AsNoTracking()
            .Include(x => x.Drafts).ThenInclude(d => d.Photos)
            .AsSplitQuery()
            .FirstAsync(x => x.Id == id, ct);
        var creatorCode = await db.Users.Where(u => u.Id == i.CreatedByUserId)
            .Select(u => u.PublicCode).FirstOrDefaultAsync(ct);
        var now = DateTimeOffset.UtcNow;

        return new InvitationDetailResponse(
            i.Id, i.ShopName, i.City, i.Category, i.InternalNote, Effective(i.Status, i.ExpiresAt, now),
            i.ExpiresAt, i.LinkIssuedAt, i.LinkVersion, i.CreatedByUserId, creatorCode, i.CreatedAt, i.AcceptedAt,
            i.Drafts.OrderBy(d => d.SortOrder).ThenBy(d => d.CreatedAt).Select(MapDraft).ToList(),
            i.IsEditable);
    }

    private InvitationDraftResponse MapDraft(InvitationDraft d) => new(
        d.Id, d.Title, d.Description, d.PriceType, d.Price, d.Category, d.SubcategoryId, d.Condition, d.SortOrder,
        d.Photos.OrderBy(p => p.SortOrder).Select(MapPhoto).ToList(), d.ListingId, d.UpdatedAt);

    private InvitationPhotoResponse MapPhoto(InvitationDraftPhoto p) => new(
        p.Id, p.SortOrder, p.Width, p.Height,
        photoUrls.Build(Request, p.Id, thumb: false), photoUrls.Build(Request, p.Id, thumb: true));

    private static object ShopSnapshot(SellerInvitation i) =>
        new { i.ShopName, i.City, i.Category, hasNote = i.InternalNote is not null };

    private static object DraftSnapshot(InvitationDraft d) =>
        new { draftId = d.Id, d.Title, d.PriceType, d.Price, d.Category, d.SubcategoryId };
}
