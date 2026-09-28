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

    /// <summary>Реестр пользователей, досье и журналируемый просмотр контактов.</summary>
    public const string UsersView = "users.view";

    /// <summary>Предупреждение пользователю (санкция без бана).</summary>
    public const string UsersWarn = "users.warn";

    /// <summary>Чёрный список банковских карт.</summary>
    public const string CardsManage = "cards.manage";

    /// <summary>Заявки на подтверждение бизнеса.</summary>
    public const string BusinessReview = "business.review";

    /// <summary>Рабочий журнал модерации (moderation_logs) и счётчики.</summary>
    public const string ModerationJournalRead = "moderation.journal.read";

    // ---- Admin ----

    /// <summary>Блокировка и разблокировка пользователей.</summary>
    public const string UsersBan = "users.ban";

    /// <summary>Справочники: города, категории, подкатегории.</summary>
    public const string CatalogManage = "catalog.manage";

    // ---- Owner ----

    /// <summary>Выдача и снятие ролей, список сотрудников, поиск по email.</summary>
    public const string RolesManage = "roles.manage";

    /// <summary>Общий журнал действий (audit_events) и история ролей.</summary>
    public const string AuditRead = "audit.read";

    public static readonly IReadOnlyList<string> All =
    [
        ListingsModerate, ReportsReview, ReviewsModerate, UsersView, UsersWarn, CardsManage,
        BusinessReview, ModerationJournalRead, UsersBan, CatalogManage, RolesManage, AuditRead
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
        string[] moderator =
        [
            Permissions.ListingsModerate, Permissions.ReportsReview, Permissions.ReviewsModerate,
            Permissions.UsersView, Permissions.UsersWarn, Permissions.CardsManage,
            Permissions.BusinessReview, Permissions.ModerationJournalRead
        ];
        string[] admin = [.. moderator, Permissions.UsersBan, Permissions.CatalogManage];
        string[] owner = [.. admin, Permissions.RolesManage, Permissions.AuditRead];

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
