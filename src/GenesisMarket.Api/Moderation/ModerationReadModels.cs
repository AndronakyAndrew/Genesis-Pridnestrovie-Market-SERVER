using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Profiles;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Moderation;

/// <summary>
/// Общие проекции экранов модератора: строка пользователя, модератор-исполнитель,
/// ссылки на объекты действий и сетевой сигнал. Одно место, чтобы журнал, жалобы
/// и реестр показывали пользователя одинаково и одинаково не показывали контакты.
/// </summary>
public static class ModerationReadModels
{
    // ---- пользователь ----

    /// <summary>Сырые поля строки пользователя (до сборки адреса аватара, которому нужен Request).</summary>
    public sealed record UserRow(
        Guid Id, string PublicCode, string DisplayName, bool HasAvatar, DateTimeOffset? ProfileUpdatedAt,
        City City, UserRole Role, DateTimeOffset CreatedAt, bool EmailVerified, bool PhoneVerified,
        bool IsBanned, DateTimeOffset? BannedUntil, int ActiveListings, int ApprovedListings,
        double? AverageRating, int ReviewsCount, int OpenReports, DateTimeOffset? LastRejectedAt,
        bool IsVerifiedBusiness, string? ShopName, DateTimeOffset? LastSeenAt, string? LastIpPrefix,
        int WarningsCount, DateTimeOffset? LastWarnedAt);

    /// <summary>
    /// Проекция пользователей в строку реестра. Счётчики — коррелированные подзапросы:
    /// на странице ≤ 100 строк это дешевле денормализации, которую пришлось бы поддерживать.
    /// </summary>
    public static IQueryable<UserRow> ProjectUsers(AppDbContext db, IQueryable<User> users) =>
        users.Select(u => new UserRow(
            u.Id,
            u.PublicCode,
            u.Profile!.DisplayName,
            u.Profile.AvatarUrl != null,
            u.Profile.UpdatedAt,
            u.Profile.City,
            u.Role,
            u.CreatedAt,
            u.EmailVerified,
            u.PhoneVerified,
            u.IsBanned,
            u.BannedUntil,
            db.Listings.Count(l => l.OwnerId == u.Id && l.Status == ListingStatus.Active),
            u.ApprovedListingsCount,
            u.AverageRating,
            u.ReviewsCount,
            db.Reports.Count(r =>
                (r.Status == ReportStatus.New || r.Status == ReportStatus.InReview) &&
                ((r.TargetType == ReportTargetType.User && r.TargetId == u.Id) ||
                 (r.TargetType == ReportTargetType.Listing &&
                  db.Listings.Any(l => l.Id == r.TargetId && l.OwnerId == u.Id)))),
            u.LastRejectedAt,
            u.BusinessProfile != null
                && u.BusinessProfile.AccountType == AccountType.Business
                && u.BusinessProfile.Status == BusinessVerificationStatus.Verified,
            u.BusinessProfile != null
                && u.BusinessProfile.AccountType == AccountType.Business
                && u.BusinessProfile.Status == BusinessVerificationStatus.Verified
                ? u.BusinessProfile.ShopName
                : null,
            db.RefreshTokens.Where(t => t.UserId == u.Id)
                .Max(t => (DateTimeOffset?)(t.LastSeenAt ?? t.CreatedAt)),
            db.RefreshTokens.Where(t => t.UserId == u.Id)
                .OrderByDescending(t => t.CreatedAt)
                .Select(t => t.IpPrefix)
                .FirstOrDefault(),
            u.WarningsCount,
            u.LastWarnedAt));

    public static ModerationUserItem ToItem(this UserRow r, HttpRequest request, int? sharedNetwork = null) => new(
        r.Id, r.PublicCode, r.DisplayName,
        r.HasAvatar ? AvatarUrl.Build(request, r.PublicCode, r.ProfileUpdatedAt) : null,
        r.City, r.Role, r.CreatedAt, r.EmailVerified, r.PhoneVerified, r.IsBanned, r.BannedUntil,
        r.ActiveListings, r.ApprovedListings, r.AverageRating, r.ReviewsCount, r.OpenReports,
        r.LastRejectedAt, r.IsVerifiedBusiness, r.ShopName, r.LastSeenAt, r.LastIpPrefix,
        SharedNetworkAccounts: sharedNetwork,
        WarningsCount: r.WarningsCount,
        LastWarnedAt: r.LastWarnedAt);

    /// <summary>Строка одного пользователя (для карточек) — вместе с сетевым сигналом.</summary>
    public static async Task<ModerationUserItem?> LoadUserAsync(
        AppDbContext db, HttpRequest request, Guid userId, CancellationToken ct)
    {
        var row = await ProjectUsers(db, db.Users.AsNoTracking().Where(u => u.Id == userId && !u.IsDeleted))
            .FirstOrDefaultAsync(ct);
        if (row is null)
            return null;
        var shared = await LinkedUserIds(db, userId).CountAsync(ct);
        return row.ToItem(request, shared);
    }

    // ---- сетевой сигнал ----

    /// <summary>
    /// Другие аккаунты, входившие с того же адреса: совпадение HMAC адреса в сессиях
    /// (<c>refresh_tokens.CreatedByIpHash</c>). Сырой IP не хранится и не нужен.
    /// Хеши старше срока хранения журналов снимает retention-джоб, так что сигнал
    /// сам ограничен последними ~90 днями.
    /// </summary>
    public static IQueryable<Guid> LinkedUserIds(AppDbContext db, Guid userId)
    {
        var myHashes = db.RefreshTokens
            .Where(t => t.UserId == userId
                        && t.CreatedByIpHash != null
                        && t.CreatedByIpHash != IpHasherExtensions.NoKeyHash)
            .Select(t => t.CreatedByIpHash);

        return db.RefreshTokens
            .Where(t => t.UserId != userId && myHashes.Contains(t.CreatedByIpHash))
            .Select(t => t.UserId)
            .Distinct();
    }

    // ---- исполнители ----

    /// <summary>Исполнители действий по Id — одним запросом на страницу.</summary>
    public static async Task<Dictionary<Guid, ModerationActor>> LoadActorsAsync(
        AppDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0)
            return [];

        return await db.Users.AsNoTracking()
            .Where(u => list.Contains(u.Id))
            .Select(u => new ModerationActor(u.Id, u.PublicCode, u.Profile!.DisplayName, u.Role))
            .ToDictionaryAsync(a => a.Id, ct);
    }

    /// <summary>Заглушка для исполнителя, которого уже нет (аккаунт удалён).</summary>
    public static ModerationActor UnknownActor(Guid id) => new(id, "—", "—", UserRole.User);

    // ---- объекты действий ----

    /// <summary>Тип объекта «отзыв» в ссылках (у журнала своего кода для отзывов нет).</summary>
    public const string TargetReview = "review";

    /// <summary>
    /// Ссылки на объекты по парам (тип, id) — по одному запросу на тип. Объект, которого
    /// больше нет, получает ссылку с <c>Exists = false</c>: журнал переживает объекты.
    /// Объявления читаются и удалённые — модератору нужна история.
    /// </summary>
    public static async Task<Dictionary<(string Type, Guid Id), ModerationTargetRef>> LoadTargetsAsync(
        AppDbContext db, IEnumerable<(string Type, Guid Id)> targets, CancellationToken ct)
    {
        var all = targets.Distinct().ToList();
        var result = new Dictionary<(string, Guid), ModerationTargetRef>();

        var listingIds = IdsOf(all, ModerationLog.TargetListing);
        if (listingIds.Count > 0)
        {
            var rows = await db.Listings.IgnoreQueryFilters().AsNoTracking()
                .Where(l => listingIds.Contains(l.Id))
                .Select(l => new
                {
                    l.Id, l.Title, l.Slug, l.Price, l.PriceType, l.City, l.Category, l.Status,
                    OwnerCode = l.Owner!.PublicCode,
                    Thumb = l.Images.OrderBy(i => i.SortOrder).Select(i => i.ThumbKey).FirstOrDefault()
                })
                .ToListAsync(ct);
            foreach (var l in rows)
                result[(ModerationLog.TargetListing, l.Id)] = new ModerationTargetRef(
                    ModerationLog.TargetListing, l.Id, l.Title, l.OwnerCode, l.Slug, l.Thumb,
                    l.Price, l.PriceType, l.City, l.Category, l.Status);
        }

        // Пользователь и заявка бизнеса адресуются Id пользователя.
        var userIds = IdsOf(all, ModerationLog.TargetUser).Concat(IdsOf(all, ModerationLog.TargetBusiness))
            .Distinct().ToList();
        if (userIds.Count > 0)
        {
            var rows = await db.Users.AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .Select(u => new
                {
                    u.Id, u.PublicCode, Name = u.Profile!.DisplayName, u.Profile.City,
                    Shop = u.BusinessProfile != null ? u.BusinessProfile.ShopName : null
                })
                .ToListAsync(ct);
            foreach (var u in rows)
            {
                result[(ModerationLog.TargetUser, u.Id)] = new ModerationTargetRef(
                    ModerationLog.TargetUser, u.Id, u.Name, u.PublicCode, City: u.City);
                result[(ModerationLog.TargetBusiness, u.Id)] = new ModerationTargetRef(
                    ModerationLog.TargetBusiness, u.Id, u.Shop ?? u.Name, u.PublicCode, City: u.City);
            }
        }

        var reportIds = IdsOf(all, ModerationLog.TargetReport);
        if (reportIds.Count > 0)
        {
            var rows = await db.Reports.AsNoTracking()
                .Where(r => reportIds.Contains(r.Id))
                .Select(r => new { r.Id, r.Reason, r.TargetType })
                .ToListAsync(ct);
            foreach (var r in rows)
                result[(ModerationLog.TargetReport, r.Id)] = new ModerationTargetRef(
                    ModerationLog.TargetReport, r.Id, r.Reason.ToString(),
                    ReportReason: r.Reason, ReportTargetType: r.TargetType);
        }

        var reviewIds = IdsOf(all, TargetReview);
        if (reviewIds.Count > 0)
        {
            var rows = await db.Reviews.AsNoTracking()
                .Where(r => reviewIds.Contains(r.Id))
                .Select(r => new { r.Id, r.Text, AuthorCode = r.Author!.PublicCode })
                .ToListAsync(ct);
            foreach (var r in rows)
                result[(TargetReview, r.Id)] = new ModerationTargetRef(
                    TargetReview, r.Id, Snippet(r.Text, 80), r.AuthorCode);
        }

        var cardIds = IdsOf(all, ModerationLog.TargetCard);
        if (cardIds.Count > 0)
        {
            var rows = await db.BlockedCards.AsNoTracking()
                .Where(c => cardIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Last4 })
                .ToListAsync(ct);
            foreach (var c in rows)
                result[(ModerationLog.TargetCard, c.Id)] = new ModerationTargetRef(
                    ModerationLog.TargetCard, c.Id, "…" + c.Last4);
        }

        // Всё, что не нашлось, — ссылка-заглушка: строка журнала не должна пропадать.
        foreach (var t in all.Where(t => !result.ContainsKey(t)))
            result[t] = new ModerationTargetRef(t.Type, t.Id, "—", Exists: false);

        return result;
    }

    /// <summary>Тип ссылки для объекта жалобы.</summary>
    public static string TargetTypeOf(ReportTargetType type) => type switch
    {
        ReportTargetType.Listing => ModerationLog.TargetListing,
        ReportTargetType.User => ModerationLog.TargetUser,
        _ => TargetReview
    };

    private static List<Guid> IdsOf(List<(string Type, Guid Id)> all, string type) =>
        all.Where(t => t.Type == type).Select(t => t.Id).ToList();

    private static string Snippet(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}
