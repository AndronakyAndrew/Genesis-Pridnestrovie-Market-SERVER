using System.Text.Json;
using GenesisMarket.Api.Audit;
using GenesisMarket.Api.Listings;
using GenesisMarket.Api.Outbox;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Imaging;
using GenesisMarket.Infrastructure.Invitations;
using GenesisMarket.Infrastructure.Persistence;
using GenesisMarket.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Invitations;

/// <summary>Состояние ссылки приглашения по токену.</summary>
public enum InvitationLinkState
{
    /// <summary>Нет такой ссылки: неизвестна, отозвана, заменена новой или уже использована.</summary>
    NotFound,

    /// <summary>Срок вышел.</summary>
    Expired,

    /// <summary>Действует.</summary>
    Active
}

public enum AcceptOutcome
{
    Accepted,
    NotFound,
    Expired,

    /// <summary>Сотрудник, подготовивший приглашение, не может принять его сам.</summary>
    OwnInvitation,

    /// <summary>Гонку выиграл другой запрос (второе «принять», отзыв, перевыпуск).</summary>
    Lost
}

public sealed record AcceptResult(AcceptOutcome Outcome, int DraftsCount = 0);

public enum PhotoUploadError
{
    None,
    Empty,
    TooLarge,
    Unsupported,
    TooManyPixels,
    LimitReached
}

/// <summary>
/// Логика приглашений продавцов, общая для служебных и публичных ручек: разбор токена,
/// загрузка фото черновика и принятие приглашения.
/// </summary>
public sealed class InvitationService(
    AppDbContext db,
    IObjectStorage storage,
    IImageProcessor imageProcessor,
    IAuditLog auditLog,
    IOptions<InvitationOptions> options)
{
    public const long MaxImageBytes = 10L * 1024 * 1024;
    public const long MaxRequestBytes = MaxImageBytes + 1024 * 1024;

    private readonly InvitationOptions _o = options.Value;

    public TimeSpan LinkLifetime => TimeSpan.FromDays(Math.Max(1, _o.LinkLifetimeDays));

    /// <summary>
    /// Приглашение по сырому токену. Форма токена проверяется до БД; «Ожидает» с вышедшим
    /// сроком — «Истекло» (фиксирует в БД джоб, здесь статус досчитывается на лету).
    /// </summary>
    public async Task<(InvitationLinkState State, SellerInvitation? Invitation)> ResolveAsync(
        string? rawToken, bool track, CancellationToken ct)
    {
        if (!InvitationTokens.IsWellFormed(rawToken))
            return (InvitationLinkState.NotFound, null);

        var hash = InvitationTokens.Hash(rawToken!);
        var query = track ? db.SellerInvitations : db.SellerInvitations.AsNoTracking();
        var invitation = await query.FirstOrDefaultAsync(i => i.TokenHash == hash, ct);

        if (invitation is null)
            return (InvitationLinkState.NotFound, null);

        return invitation.IsLinkActive(DateTimeOffset.UtcNow)
            ? (InvitationLinkState.Active, invitation)
            : invitation.Status is InvitationStatus.Pending or InvitationStatus.Expired
                ? (InvitationLinkState.Expired, invitation)
                : (InvitationLinkState.NotFound, null);
    }

    /// <summary>
    /// Фото черновика: тот же конвейер, что у объявления (тип по сигнатуре, снятие метаданных,
    /// перекодирование), но под префиксом <c>invitations/{id}/</c>, который публичная выдача
    /// не обслуживает. Сохранение строки — на вызывающем (вместе с записью журнала).
    /// </summary>
    public async Task<(PhotoUploadError Error, InvitationDraftPhoto? Photo)> UploadPhotoAsync(
        InvitationDraft draft, IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return (PhotoUploadError.Empty, null);
        if (file.Length > MaxImageBytes)
            return (PhotoUploadError.TooLarge, null);

        var count = await db.InvitationDraftPhotos.CountAsync(p => p.DraftId == draft.Id, ct);
        if (count >= _o.MaxPhotosPerDraft)
            return (PhotoUploadError.LimitReached, null);

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);

        ProcessedImage processed;
        try
        {
            processed = await imageProcessor.ProcessAsync(buffer, ct);
        }
        catch (UnsupportedImageFormatException)
        {
            return (PhotoUploadError.Unsupported, null);
        }
        catch (ImageTooLargeException)
        {
            return (PhotoUploadError.TooManyPixels, null);
        }

        var key = Guid.CreateVersion7();
        var objectKey = $"invitations/{draft.InvitationId}/{key}.webp";
        var thumbKey = $"invitations/{draft.InvitationId}/{key}_thumb.webp";

        await using (var os = new MemoryStream(processed.Original))
            await storage.PutAsync(objectKey, os, os.Length, "image/webp", ct);
        await using (var ts = new MemoryStream(processed.Thumbnail))
            await storage.PutAsync(thumbKey, ts, ts.Length, "image/webp", ct);

        var maxOrder = await db.InvitationDraftPhotos
            .Where(p => p.DraftId == draft.Id)
            .Select(p => (int?)p.SortOrder)
            .MaxAsync(ct);

        var photo = new InvitationDraftPhoto
        {
            Id = key,
            DraftId = draft.Id,
            ObjectKey = objectKey,
            ThumbKey = thumbKey,
            SortOrder = (maxOrder ?? -1) + 1,
            Width = processed.Width,
            Height = processed.Height
        };
        db.InvitationDraftPhotos.Add(photo);
        return (PhotoUploadError.None, photo);
    }

    /// <summary>Поставить удаление объектов фото в outbox (в текущий DbContext, без сохранения).</summary>
    public void EnqueuePhotoDeletion(IEnumerable<InvitationDraftPhoto> photos)
    {
        var keys = photos.SelectMany(p => new[] { p.ObjectKey, p.ThumbKey }).ToArray();
        if (keys.Length == 0)
            return;
        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = OutboxMessage.DeleteImages,
            Payload = JsonSerializer.Serialize(keys)
        });
    }

    /// <summary>
    /// Принятие приглашения — одна транзакция:
    /// <list type="number">
    /// <item>условный UPDATE «Ожидает → Принято» по хешу токена и сроку: из двух одновременных
    /// «принять» (или «принять» против «отозвать»/«перевыпустить») строку меняет ровно один,
    /// второй получает 0 строк и откатывается — черновики не раздваиваются;</item>
    /// <item>каждый черновик становится объявлением владельца в статусе Draft — не публикуется;</item>
    /// <item>фото получают строки <see cref="ListingImage"/> с новыми ключами под <c>listings/</c>,
    /// а сами объекты переносит outbox после фиксации;</item>
    /// <item>запись в журнал и письмо владельцу — в той же транзакции.</item>
    /// </list>
    /// </summary>
    public async Task<AcceptResult> AcceptAsync(string? rawToken, Guid ownerId, CancellationToken ct)
    {
        var (state, found) = await ResolveAsync(rawToken, track: false, ct);
        if (state == InvitationLinkState.NotFound)
            return new AcceptResult(AcceptOutcome.NotFound);
        if (state == InvitationLinkState.Expired)
            return new AcceptResult(AcceptOutcome.Expired);
        if (found!.CreatedByUserId == ownerId)
            return new AcceptResult(AcceptOutcome.OwnInvitation);

        var hash = found.TokenHash!;
        var now = DateTimeOffset.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var won = await db.SellerInvitations
            .Where(i => i.Id == found.Id && i.Status == InvitationStatus.Pending
                        && i.TokenHash == hash && i.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, InvitationStatus.Accepted)
                .SetProperty(i => i.AcceptedByUserId, ownerId)
                .SetProperty(i => i.AcceptedAt, now)
                .SetProperty(i => i.TokenHash, (byte[]?)null)
                .SetProperty(i => i.StatusChangedAt, now)
                .SetProperty(i => i.UpdatedAt, now), ct);
        if (won != 1)
            return new AcceptResult(AcceptOutcome.Lost);

        // Витрина хранится отдельно и не заменяет личное имя/город владельца.
        var businessProfile = await db.BusinessProfiles.FirstOrDefaultAsync(b => b.UserId == ownerId, ct);
        if (businessProfile is null)
            db.BusinessProfiles.Add(BusinessProfile.CreateInvitedStore(ownerId, found.ShopName, found.City, now));
        else
            businessProfile.MarkAsInvitedStore(found.ShopName, found.City, now);

        var drafts = await db.InvitationDrafts
            .Include(d => d.Photos)
            .Where(d => d.InvitationId == found.Id)
            .OrderBy(d => d.SortOrder).ThenBy(d => d.CreatedAt)
            .ToListAsync(ct);

        var moves = new List<MoveImagePair>();
        foreach (var draft in drafts)
        {
            var listing = new Listing
            {
                Slug = "",
                Title = draft.Title,
                Description = draft.Description,
                Price = draft.Price,
                PriceType = draft.PriceType,
                Category = draft.Category,
                SubcategoryId = draft.SubcategoryId,
                City = found.City,
                Condition = draft.Condition,
                Status = ListingStatus.Draft,
                OwnerId = ownerId,
                SourceInvitationId = found.Id,
                PreparedByUserId = draft.UpdatedByUserId
            };
            listing.Slug = SlugGenerator.Generate(listing.Title, SlugGenerator.SuffixFromId(listing.Id));
            db.Listings.Add(listing);

            foreach (var photo in draft.Photos.OrderBy(p => p.SortOrder))
            {
                var key = Guid.CreateVersion7();
                var objectKey = $"listings/{listing.Id}/{key}.webp";
                var thumbKey = $"listings/{listing.Id}/{key}_thumb.webp";
                db.ListingImages.Add(new ListingImage
                {
                    ListingId = listing.Id,
                    ObjectKey = objectKey,
                    ThumbKey = thumbKey,
                    SortOrder = photo.SortOrder,
                    Width = photo.Width,
                    Height = photo.Height
                });
                moves.Add(new MoveImagePair(photo.ObjectKey, objectKey));
                moves.Add(new MoveImagePair(photo.ThumbKey, thumbKey));
            }

            draft.ListingId = listing.Id;
        }

        // Строки фото черновиков больше не нужны: объекты переезжают, а ключи уже в listing_images.
        db.InvitationDraftPhotos.RemoveRange(drafts.SelectMany(d => d.Photos));

        if (moves.Count > 0)
            db.OutboxMessages.Add(new OutboxMessage
            {
                Type = OutboxMessage.MoveImages,
                Payload = JsonSerializer.Serialize(moves)
            });

        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = OutboxMessage.InvitationAccepted,
            Payload = JsonSerializer.Serialize(new { invitationId = found.Id })
        });

        auditLog.Record(AuditActions.InvitationAccept, AuditActions.TargetInvitation, found.Id.ToString(),
            oldValue: new { status = nameof(InvitationStatus.Pending) },
            newValue: new
            {
                status = nameof(InvitationStatus.Accepted),
                ownerId,
                preparedBy = found.CreatedByUserId,
                listings = drafts.Select(d => d.ListingId).ToArray()
            },
            actingUserId: ownerId);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new AcceptResult(AcceptOutcome.Accepted, drafts.Count);
    }
}
