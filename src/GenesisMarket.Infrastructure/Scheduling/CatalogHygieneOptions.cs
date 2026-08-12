namespace GenesisMarket.Infrastructure.Scheduling;

/// <summary>
/// Пороги «гигиены каталога»: сроки автоархивации/предупреждения и окна ручных
/// переходов (поднятие, возврат из продажи, восстановление, повторная премодерация).
/// Секция конфигурации <c>CatalogHygiene</c>. Используется и джобом (Infrastructure),
/// и эндпоинтами объявлений (Api).
/// </summary>
public sealed class CatalogHygieneOptions
{
    public const string Section = "CatalogHygiene";

    /// <summary>Через сколько дней бездействия (coalesce(BumpedAt, PublishedAt)) объявление уходит в архив.</summary>
    public int ArchiveAfterDays { get; set; } = 30;

    /// <summary>За сколько дней до архивации автору уходит предупреждение со ссылкой «продлить».</summary>
    public int WarnBeforeDays { get; set; } = 3;

    /// <summary>Не чаще одного поднятия в стольких днях на объявление.</summary>
    public int BumpCooldownDays { get; set; } = 7;

    /// <summary>В течение стольких дней после продажи разрешён обратный переход Sold → Active.</summary>
    public int SoldReactivationDays { get; set; } = 7;

    /// <summary>Восстановление из архива разрешено, если с момента архивации прошло не более стольких дней.</summary>
    public int RestoreWithinDays { get; set; } = 90;

    /// <summary>
    /// Если пользователь получал reject за последние столько дней — восстановление
    /// объявления проходит премодерацию заново.
    /// </summary>
    public int RejectLookbackDays { get; set; } = 30;

    /// <summary>Сколько объявлений джоб обрабатывает за один проход (батч), чтобы не грузить всё разом.</summary>
    public int BatchSize { get; set; } = 200;
}
