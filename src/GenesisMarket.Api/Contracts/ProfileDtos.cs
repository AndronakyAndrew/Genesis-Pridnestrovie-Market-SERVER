using System.ComponentModel.DataAnnotations;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Contracts;

/// <summary>
/// Приватный профиль владельца — всё, кроме PasswordHash и SecurityStamp.
///
/// <see cref="PublicCode"/> — пять голых цифр. Оформление («GEN-82914»,
/// «#GEN-82914») — забота клиента: префикс это брендинг, а не данные, и API
/// не должен переучиваться при его смене.
/// </summary>
public record MeResponse(
    Guid Id,
    string PublicCode,
    string Email,
    UserRole Role,
    string? PhoneE164,
    bool PhoneVerified,
    bool EmailVerified,
    bool IsBanned,
    DateTimeOffset? BannedUntil,
    bool IsDeleted,
    string DisplayName,
    City City,
    string? AvatarUrl,
    string? TelegramUsername,
    string? Description,
    bool ViberEnabled,
    bool WhatsappEnabled,
    bool ShowPhoneInListing,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

/// <summary>
/// PATCH профиля. ФИКСИРОВАННЫЙ набор полей — защита от mass assignment:
/// Role, IsBanned, PasswordHash, SecurityStamp, Email сюда не входят и не
/// редактируются ни при каких условиях. Все поля опциональны (обновляется
/// только переданное).
///
/// <see cref="Description"/> («О себе») перед записью нормализуется
/// (<see cref="Profiles.ProfileText.NormalizeDescription"/>): пробелы по краям
/// снимаются, пустой текст сохраняется как NULL. Лимит 300 проверяется по сырому
/// вводу — прислать 301 символ нельзя даже пробелами.
/// </summary>
public record UpdateMeRequest(
    [MinLength(2), MaxLength(60)] string? DisplayName,
    City? City,
    [MaxLength(20)] string? PhoneE164,
    [MaxLength(64)] string? TelegramUsername,
    [MaxLength(300)] string? Description,
    bool? ViberEnabled,
    bool? WhatsappEnabled,
    bool? ShowPhoneInListing);

public record AvatarResponse(string AvatarUrl);

/// <summary>
/// Активная сессия владельца для вкладки «Безопасность».
///
/// Наружу отдаётся только то, по чему владелец узнаёт СВОЁ устройство, и ничего
/// сверх: ни сырого User-Agent (он не хранится вовсе), ни полного IP (он есть
/// только как HMAC и наружу не выходит никогда), ни геопозиции.
/// </summary>
public record SessionResponse(
    /// <summary>Устойчивый идентификатор сессии: переживает ротацию refresh-токена.</summary>
    Guid Id,
    string? DeviceFamily,
    string? BrowserFamily,
    string? OsFamily,
    /// <summary>Два октета IPv4 («185.112») или два хекстета IPv6. Не полный адрес.</summary>
    string? IpPrefix,
    /// <summary>
    /// Город. Всегда null — см. TODO в SessionsController: определение города
    /// требует базы GeoIP и превращает список сессий в журнал перемещений.
    /// Поле оставлено в контракте, чтобы его появление не ломало клиента.
    /// </summary>
    string? City,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    /// <summary>Сессия, из которой пришёл текущий запрос (сверка по claim sid).</summary>
    bool IsCurrent);

/// <summary>
/// Публичный профиль продавца. Ни email, ни телефона, ни точной даты
/// регистрации, ни Role, ни IsBanned. Дата регистрации — только месяц и год
/// (<see cref="RegisteredAt"/> — всегда первое число месяца).
/// <see cref="Description"/> («О себе») — свободный текст продавца, публичен
/// по замыслу: его пишет сам продавец для покупателей.
///
/// <see cref="PublicCode"/> — тот самый «ID профиля» («GEN-82914»), которым
/// пользователи и модераторы называют аккаунт вместо GUID. Отдаётся всем: это
/// его назначение. Он же теперь единственный публичный идентификатор аккаунта —
/// Guid из публичных ответов и адресов убран (см. комментарий к UsersController).
///
/// Важное следствие: кодов всего 90 000, и ручка, принимающая код на ВХОД,
/// превращает его в перечислимое пространство имён — обход «10000…99999» выдал бы
/// список аккаунтов площадки. Поэтому поиск по коду живёт только под политикой
/// Moderator (<c>GET /api/moderation/users/by-code/{code}</c>), а анонимных
/// резолверов «код → профиль» быть не должно.
/// </summary>
public record PublicProfileResponse(
    string PublicCode,
    string DisplayName,
    City City,
    string? AvatarUrl,
    string? Description,
    DateOnly RegisteredAt,
    int ActiveListingsCount,
    double? AverageRating,
    int ReviewsCount,
    bool PhoneVerified,
    /// <summary>Подтверждённый бизнес — основание для бейджа (то же условие, что в карточке объявления).</summary>
    bool IsVerifiedBusiness = false,
    /// <summary>
    /// Название магазина и реквизиты. Только у подтверждённого бизнеса; иначе null —
    /// непроверенные реквизиты частного лица публичными не становятся.
    /// </summary>
    PublicBusinessInfo? Business = null,
    /// <summary>
    /// Почта подтверждена — основание для галочки «Проверенный пользователь».
    /// Телефон для галочки не требуется; PhoneVerified остаётся отдельным фактом.
    /// </summary>
    bool EmailVerified = false);
