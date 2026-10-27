namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Настройка отключаемой группы уведомлений. Отсутствующая строка означает enabled=true,
/// чтобы новые группы включались по умолчанию без обновления существующих пользователей.
/// </summary>
public class AccountNotificationPreference
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public required string GroupKey { get; set; }
    public bool Enabled { get; set; }
}
