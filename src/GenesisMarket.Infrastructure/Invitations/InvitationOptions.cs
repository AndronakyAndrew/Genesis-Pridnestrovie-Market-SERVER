namespace GenesisMarket.Infrastructure.Invitations;

/// <summary>Настройки приглашений продавцов. Секция <c>Invitations</c>.</summary>
public sealed class InvitationOptions
{
    public const string Section = "Invitations";

    /// <summary>Срок действия ссылки, дни.</summary>
    public int LinkLifetimeDays { get; set; } = 14;

    /// <summary>Через сколько дней удаляются черновики и фото приглашений «Истекло»/«Отозвано».</summary>
    public int ClosedRetentionDays { get; set; } = 30;

    /// <summary>Через сколько дней без изменений удаляется брошенное «Готовится».</summary>
    public int AbandonedDraftDays { get; set; } = 60;

    public int MaxDraftsPerInvitation { get; set; } = 50;

    public int MaxPhotosPerDraft { get; set; } = 8;

    /// <summary>
    /// Версия текста согласия на публикацию. Меняется вместе с текстом на фронтенде;
    /// согласие старой версии для новой публикации не засчитывается.
    /// </summary>
    public string ConsentVersion { get; set; } = "2026-10-01";

    /// <summary>Сколько живёт подписанная ссылка на фото черновика, минуты.</summary>
    public int PhotoUrlMinutes { get; set; } = 60;

    /// <summary>Cron фоновой очистки (Quartz).</summary>
    public string CleanupCron { get; set; } = "0 20 3 * * ?";
}
