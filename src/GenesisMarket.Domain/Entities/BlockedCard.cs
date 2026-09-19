namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Номер банковской карты в чёрном списке модерации (карта, на которую мошенник
/// требует «предоплату»). Сам номер НЕ хранится: только HMAC-SHA256 от цифр
/// (<see cref="CardHash"/>) — по нему сверяется текст объявления — и последние
/// четыре цифры (<see cref="Last4"/>), чтобы модератор узнавал запись в списке.
/// Номер чужой карты — персональные данные третьего лица; в открытом виде он
/// здесь не нужен ни для одной операции.
/// </summary>
public class BlockedCard
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>HMAC-SHA256 (hex) от нормализованных цифр номера. Уникален.</summary>
    public required string CardHash { get; set; }

    /// <summary>Последние 4 цифры — для узнавания записи человеком.</summary>
    public required string Last4 { get; set; }

    /// <summary>Почему карта в списке (до 500 символов).</summary>
    public required string Reason { get; set; }

    /// <summary>Жалоба, из-за которой карту внесли. null — внесена вручную.</summary>
    public Guid? SourceReportId { get; set; }

    /// <summary>Модератор, внёсший карту.</summary>
    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
