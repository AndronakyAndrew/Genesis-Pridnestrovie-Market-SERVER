using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Auth;

/// <summary>
/// Разрешения служебной зоны. Каждое — имя политики авторизации: контроллеры
/// проверяют разрешение (<c>[Authorize(Policy = Permissions.UsersBan)]</c>), а не имя
/// роли. Роль → разрешения — одна карта в <see cref="RolePermissions"/>; при переносе
/// ролей в Genesis Ops меняется источник роли, а не проверки в эндпоинтах.
/// </summary>
public static class Permissions
{
    // ---- Moderator ----

    /// <summary>Очередь и решения по объявлениям (одобрить/отклонить/доработка/назначить).</summary>
    public const string ListingsModerate = "listings.moderate";

    /// <summary>Жалобы: список, взять в работу, закрыть.</summary>
    public const string ReportsReview = "reports.review";

    /// <summary>Скрытие отзывов.</summary>
    public const string ReviewsModerate = "reviews.moderate";

    /// <summary>Предупреждение пользователю (санкция без бана) — только к роли ниже своей.</summary>
    public const string UsersWarn = "users.warn";

    // ---- Admin ----

    /// <summary>Реестр пользователей и досье (без контактов).</summary>
    public const string UsersView = "users.view";

    /// <summary>Чёрный список банковских карт.</summary>
    public const string CardsManage = "cards.manage";

    /// <summary>Заявки на подтверждение бизнеса.</summary>
    public const string BusinessReview = "business.review";

    /// <summary>Рабочий журнал модерации (moderation_logs): все решения всех сотрудников.</summary>
    public const string ModerationJournalRead = "moderation.journal.read";

    /// <summary>Блокировка и разблокировка пользователей.</summary>
    public const string UsersBan = "users.ban";

    /// <summary>Справочники: города, категории, подкатегории.</summary>
    public const string CatalogManage = "catalog.manage";

    /// <summary>
    /// Контакты (email, телефон) любого пользователя в рамках модерации, с записью в журнал.
    /// Без него — только контакты участников жалобы, которую сотрудник сейчас разбирает.
    /// </summary>
    public const string UsersContacts = "users.contacts";

    /// <summary>Приглашения продавцов: создавать и вести СВОИ (чужие — только с <see cref="InvitationsAll"/>).</summary>
    public const string InvitationsManage = "invitations.manage";

    // ---- Owner ----

    /// <summary>Выдача и снятие ролей, список сотрудников, поиск по email.</summary>
    public const string RolesManage = "roles.manage";

    /// <summary>Общий журнал действий (audit_events) и история ролей.</summary>
    public const string AuditRead = "audit.read";

    /// <summary>Видеть, отзывать и перевыпускать приглашения любого сотрудника.</summary>
    public const string InvitationsAll = "invitations.all";

    public static readonly IReadOnlyList<string> All =
    [
        ListingsModerate, ReportsReview, ReviewsModerate, UsersView, UsersWarn, CardsManage,
        BusinessReview, ModerationJournalRead, UsersBan, CatalogManage, UsersContacts, InvitationsManage,
        RolesManage, AuditRead, InvitationsAll
    ];
}

/// <summary>
/// Карта «роль → разрешения». Роли кумулятивны: каждая следующая по рангу
/// (<see cref="UserRole"/>) получает всё, что у предыдущей, плюс своё.
/// </summary>
public static class RolePermissions
{
    private static readonly IReadOnlyDictionary<UserRole, IReadOnlySet<string>> Map = Build();

    private static Dictionary<UserRole, IReadOnlySet<string>> Build()
    {
        // Модератор — объявления, жалобы, отзывы и предупреждение (эталонная матрица, 2026-09-28).
        string[] moderator =
        [
            Permissions.ListingsModerate, Permissions.ReportsReview, Permissions.ReviewsModerate,
            Permissions.UsersWarn
        ];
        string[] admin =
        [
            .. moderator, Permissions.UsersView, Permissions.CardsManage, Permissions.BusinessReview,
            Permissions.ModerationJournalRead, Permissions.UsersBan, Permissions.CatalogManage,
            Permissions.UsersContacts, Permissions.InvitationsManage
        ];
        string[] owner = [.. admin, Permissions.RolesManage, Permissions.AuditRead, Permissions.InvitationsAll];

        return new Dictionary<UserRole, IReadOnlySet<string>>
        {
            [UserRole.User] = new HashSet<string>(),
            [UserRole.Moderator] = moderator.ToHashSet(),
            [UserRole.Admin] = admin.ToHashSet(),
            [UserRole.Owner] = owner.ToHashSet()
        };
    }

    public static IReadOnlySet<string> For(UserRole role) => Map[role];

    public static bool Has(UserRole? role, string permission) =>
        role is { } r && Map.TryGetValue(r, out var set) && set.Contains(permission);

    /// <summary>Служебная роль: для неё обязательна 2FA и действует короткая сессия.</summary>
    public static bool IsStaff(UserRole role) => role >= UserRole.Moderator;

    /// <summary>
    /// Может ли исполнитель применять санкции к цели: только к роли строго ниже своей.
    /// Модератор не предупреждает модератора, админ не банит админа, владельца не
    /// трогает никто.
    /// </summary>
    public static bool Outranks(UserRole actor, UserRole target) => actor > target;
}
