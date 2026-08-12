namespace GenesisMarket.Infrastructure.Scheduling;

/// <summary>
/// Настройки диспетчера и уборщика outbox. Секция <c>Outbox</c> (переопределяется env
/// как <c>Outbox__Ключ</c>). Значения по умолчанию рассчитаны на один-несколько инстансов API.
/// </summary>
public sealed class OutboxOptions
{
    public const string Section = "Outbox";

    /// <summary>Интервал опроса диспетчера, секунды. По умолчанию 10 с.</summary>
    public int DispatchIntervalSeconds { get; set; } = 10;

    /// <summary>Размер батча за один тик. По умолчанию 50.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>CRON уборщика (Quartz-формат). По умолчанию ежедневно в 03:30 UTC.</summary>
    public string CleanupCron { get; set; } = "0 30 3 * * ?";

    /// <summary>Сколько дней хранить доставленные (Done) сообщения. По умолчанию 30.</summary>
    public int RetentionDays { get; set; } = 30;
}
