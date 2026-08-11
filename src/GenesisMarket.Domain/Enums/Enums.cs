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
    Dnestrovsk
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
