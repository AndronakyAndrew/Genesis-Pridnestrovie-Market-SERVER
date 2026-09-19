namespace GenesisMarket.Domain.Enums;

// ВНИМАНИЕ: значения (метки) этих enum-ов в PostgreSQL — native enum-типы
// (CREATE TYPE ... AS ENUM), а не строки/числа. Метки формируются из имён
// членов приведением к нижнему регистру (RealEstate -> "realestate"),
// чтобы совпадать со справочниками из CLAUDE.md.
// Порядок членов менять нельзя без миграции ALTER TYPE.

/// <summary>Города ПМР. Native enum-тип <c>city</c>.</summary>
public enum City
{
    Tiraspol,
    Bendery,
    Rybnitsa,
    Dubossary,
    Slobodzea,
    Grigoriopol,
    Dnestrovsk,
    Kamenka
}

/// <summary>Категории каталога. Native enum-тип <c>category</c>.</summary>
public enum Category
{
    RealEstate,
    Transport,
    Electronics,
    Home,
    Fashion,
    Kids,
    Work,
    Services,
    Animals,
    Other
}

/// <summary>Состояние товара. Native enum-тип <c>condition</c>.</summary>
public enum Condition
{
    New,
    Used,
    NotApplicable
}

/// <summary>
/// Статус объявления. Native enum-тип <c>listing_status</c>.
/// Draft → PendingReview → Active → Sold/Archived; Rejected — ветка модерации.
/// </summary>
public enum ListingStatus
{
    Draft,
    PendingReview,
    Active,
    Sold,
    Archived,
    Rejected
}

/// <summary>
/// Тип цены. Native enum-тип <c>price_type</c>.
/// Fixed — фиксированная (Price ≥ 0); Negotiable — договорная (Price = null);
/// Free — бесплатно (Price = 0). Согласованность гарантируется CHECK-констрейнтом.
/// </summary>
public enum PriceType
{
    Fixed,
    Negotiable,
    Free
}

/// <summary>
/// Роль пользователя. В БД хранится строкой (не входит в набор native enum-ов).
/// </summary>
public enum UserRole
{
    User,
    Moderator,
    Admin
}

/// <summary>
/// Канал подтверждения (код). В БД хранится строкой.
/// Единый механизм для почты и телефона.
/// </summary>
public enum VerificationChannel
{
    Email,
    Phone
}

/// <summary>
/// Тип объекта жалобы. В БД хранится строкой (не входит в native enum-ы каталога).
/// </summary>
public enum ReportTargetType
{
    Listing,
    User,
    Review
}

/// <summary>
/// Причина отклонения объявления модератором. В БД хранится строкой.
///
/// Отдельный от <see cref="ReportReason"/> набор намеренно: жалоба и отклонение
/// отвечают на разные вопросы. Жалоба — «на что пожаловался посетитель» (Spam,
/// Fraud), отклонение — «что автору исправить, чтобы объявление прошло».
/// Поэтому здесь есть BadPhotos и ContactsInText, которых нет у жалоб, и нет
/// Spam/Fraud: по ним объявление не исправляют, а банят.
/// </summary>
public enum RejectionReasonCode
{
    /// <summary>Такое объявление у автора уже есть.</summary>
    Duplicate,

    /// <summary>Товар или услуга запрещены к размещению.</summary>
    ProhibitedItem,

    /// <summary>Выбрана не та категория или подкатегория.</summary>
    WrongCategory,

    /// <summary>Фото нечитаемы, чужие или не показывают товар.</summary>
    BadPhotos,

    /// <summary>Телефон/мессенджер в заголовке или описании (в обход контактов профиля).</summary>
    ContactsInText,

    /// <summary>Цена не соответствует товару либо указана для привлечения внимания.</summary>
    PriceViolation,

    /// <summary>Прочее. Требует комментария модератора — код сам по себе ничего не объясняет.</summary>
    Other
}

/// <summary>Причина жалобы. В БД хранится строкой.</summary>
public enum ReportReason
{
    Spam,
    Fraud,
    Prohibited,
    WrongCategory,
    Duplicate,
    PriceViolation,
    Other
}

/// <summary>
/// Статус обработки жалобы. В БД хранится строкой.
/// New → InReview → Resolved; ветка Rejected (жалоба отклонена модератором).
/// </summary>
public enum ReportStatus
{
    New,
    InReview,
    Resolved,
    Rejected
}

/// <summary>
/// Статус сообщения транзакционного outbox. В БД хранится строкой.
/// Pending → (Processing) → Done; ветка Failed — исчерпаны попытки, сообщение
/// оставлено для разбора и повторно не выбирается.
/// </summary>
public enum OutboxStatus
{
    /// <summary>Ждёт отправки (в т.ч. между ретраями — по <c>NextAttemptAt</c>).</summary>
    Pending,

    /// <summary>Взято диспетчером в обработку (транзиентно, внутри одного тика).</summary>
    Processing,

    /// <summary>Успешно доставлено.</summary>
    Done,

    /// <summary>Исчерпаны попытки. Терминальный статус; строка сохраняется для разбора.</summary>
    Failed
}

/// <summary>
/// Статус публикации объявления в Telegram-канал (<c>ChannelPostQueue</c>). В БД хранится строкой.
/// Pending → Published; ветки Failed (попытки исчерпаны) и Skipped (публиковать не нужно —
/// объявление снято, канал для категории не задан и т. п.).
/// </summary>
public enum ChannelPostStatus
{
    /// <summary>Ждёт публикации.</summary>
    Pending,

    /// <summary>Пост отправлен в канал. Терминальный статус.</summary>
    Published,

    /// <summary>Исчерпаны попытки. Терминальный статус; причина — в <c>LastError</c>.</summary>
    Failed,

    /// <summary>Публикация сознательно не выполнялась. Терминальный статус.</summary>
    Skipped
}

/// <summary>
/// Канал доставки уведомлений пользователю. В БД (настройка профиля) хранится строкой.
/// </summary>
public enum NotificationChannel
{
    /// <summary>Электронная почта (SMTP). Канал по умолчанию — адрес есть у каждого аккаунта.</summary>
    Email,

    /// <summary>Личные сообщения Telegram. Требует привязанного <c>TelegramChatId</c>.</summary>
    Telegram
}

/// <summary>
/// Канал уведомлений сохранённого поиска. В БД хранится строкой. В отличие от
/// <see cref="NotificationChannel"/> допускает <see cref="None"/> — поиск сохранён,
/// но рассылка по нему выключена (джоб такие поиски пропускает целиком).
/// </summary>
public enum SavedSearchNotifyChannel
{
    /// <summary>Слать письмом на адрес аккаунта.</summary>
    Email,

    /// <summary>Слать в Telegram (при отсутствии <c>TelegramChatId</c> деградирует к почте).</summary>
    Telegram,

    /// <summary>Не уведомлять. Поиск активен для ручного просмотра, но джоб его не рассылает.</summary>
    None
}

/// <summary>Тип обращения формы обратной связи. В БД хранится строкой.</summary>
public enum FeedbackType
{
    General,
    Bug,
    Complaint,
    Partnership
}

/// <summary>
/// Режим публикации, выбранный политикой модерации. В БД НЕ хранится: это решение
/// на момент публикации, его материализация — пара полей объявления
/// (<c>Status</c> + <c>ReviewQueuedAt</c>).
/// </summary>
public enum PublishMode
{
    /// <summary>
    /// Сразу в каталог, без проверки: автор доверен и контент не рисковый.
    /// Объявление получает <c>ApprovedAt</c> и идёт в зачёт доверия автора.
    /// </summary>
    Auto,

    /// <summary>
    /// Постмодерация: сразу в каталог, но в очередь модератора на последующую проверку.
    /// <c>ApprovedAt</c> НЕ проставляется — доверие растёт только после решения человека.
    /// </summary>
    PostReview,

    /// <summary>Премодерация: в каталог не попадает до одобрения модератором.</summary>
    PreReview
}

/// <summary>
/// Тип аккаунта. В БД (<c>business_profiles.AccountType</c>) хранится строкой.
/// Отдельной регистрации для бизнеса нет: это режим обычного пользователя,
/// включается сохранением реквизитов (<c>PUT /api/account/business</c>).
/// Нет строки в <c>business_profiles</c> ⇒ <see cref="Private"/>.
/// </summary>
public enum AccountType
{
    Private,
    Business
}

/// <summary>Организационно-правовая форма бизнеса. В БД хранится строкой.</summary>
public enum LegalForm
{
    /// <summary>Индивидуальный предприниматель (ЕГРИП).</summary>
    IndividualEntrepreneur,

    /// <summary>Общество с ограниченной ответственностью (ЕГРЮЛ).</summary>
    LimitedLiabilityCompany,

    /// <summary>Иная форма (ЗАО, ГУП, КФХ и т. п.).</summary>
    Other
}

/// <summary>
/// Статус проверки бизнес-реквизитов. В БД хранится строкой.
/// None → Pending (подача пользователем) → Verified | Rejected (решение модератора);
/// Rejected → Pending (повторная подача после исправления).
/// Verified и Rejected выставляет ТОЛЬКО модератор — публичного пути к ним нет.
/// </summary>
public enum BusinessVerificationStatus
{
    /// <summary>Реквизиты не подавались на проверку либо проверка снята (правка/возврат в Private).</summary>
    None,

    /// <summary>Заявка ждёт модератора. Реквизиты только на чтение.</summary>
    Pending,

    /// <summary>Подтверждено модератором — даёт бейдж «подтверждённый бизнес».</summary>
    Verified,

    /// <summary>Отклонено модератором с причиной. Можно исправить и подать повторно.</summary>
    Rejected
}
