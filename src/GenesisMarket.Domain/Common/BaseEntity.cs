namespace GenesisMarket.Domain.Common;

/// <summary>
/// Базовая сущность: суррогатный ключ (UUIDv7) и аудит создания/изменения.
/// Все временные метки — UTC, в коде <see cref="DateTimeOffset"/>,
/// в БД — timestamptz. Форматирование даты — забота клиента.
/// </summary>
public abstract class BaseEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? UpdatedAt { get; set; }
}
