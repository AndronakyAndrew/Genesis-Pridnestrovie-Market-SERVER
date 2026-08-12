using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace GenesisMarket.Infrastructure.Scheduling;

public static class SchedulingServiceCollectionExtensions
{
    /// <summary>
    /// Планировщик Quartz с persistent job store в PostgreSQL и ежедневным джобом
    /// гигиены каталога. Регистрацию можно выключить (<c>Scheduling:Enabled=false</c>) —
    /// например, в тестах, где джоб прогоняют напрямую через <see cref="ICatalogHygieneService"/>.
    /// </summary>
    public static IServiceCollection AddScheduling(
        this IServiceCollection services,
        IConfiguration configuration,
        string postgresConnectionString)
    {
        services.Configure<CatalogHygieneOptions>(configuration.GetSection(CatalogHygieneOptions.Section));
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.Section));
        services.Configure<SavedSearchOptions>(configuration.GetSection(SavedSearchOptions.Section));

        // Сервис-логика доступна всегда (в т.ч. когда планировщик выключен — для тестов/ручного прогона).
        services.AddScoped<ICatalogHygieneService, CatalogHygieneService>();

        var enabled = configuration.GetValue($"{SchedulingSection}:Enabled", defaultValue: true);
        if (!enabled)
            return services;

        // CRON запуска (Quartz-формат: сек мин час ...). По умолчанию — ежедневно в 03:00 UTC.
        var cron = configuration[$"{SchedulingSection}:HygieneCron"];
        if (string.IsNullOrWhiteSpace(cron))
            cron = "0 0 3 * * ?";

        var outbox = configuration.GetSection(OutboxOptions.Section).Get<OutboxOptions>() ?? new OutboxOptions();
        var dispatchInterval = Math.Max(1, outbox.DispatchIntervalSeconds);

        var savedSearch = configuration.GetSection(SavedSearchOptions.Section).Get<SavedSearchOptions>()
                          ?? new SavedSearchOptions();
        var savedSearchCron = string.IsNullOrWhiteSpace(savedSearch.NotificationCron)
            ? "0 0/15 * * * ?"
            : savedSearch.NotificationCron;

        services.AddQuartz(q =>
        {
            q.SchedulerName = "genesis-scheduler";
            // AUTO — уникальный instanceId на узел (обязательно при кластеризации).
            q.SchedulerId = "AUTO";

            // Persistent job store: триггеры/состояние переживают рестарт; поддерживает кластер.
            q.UsePersistentStore(store =>
            {
                store.UseProperties = true;          // JobDataMap — только строки (без бинарной сериализации)
                store.UsePostgres(postgresConnectionString);
                store.UseSystemTextJsonSerializer();
                store.UseClustering();               // блокировка джоба на уровне БД (и на одном узле безопасно)
            });

            q.AddJob<CatalogHygieneJob>(j => j
                .WithIdentity(CatalogHygieneJob.Key)
                .StoreDurably()
                .WithDescription("Автоархивация объявлений и предупреждения авторов"));

            q.AddTrigger(t => t
                .ForJob(CatalogHygieneJob.Key)
                .WithIdentity("catalog-hygiene-daily")
                .WithCronSchedule(cron, x => x.WithMisfireHandlingInstructionDoNothing()));

            // ---- Транзакционный outbox: диспетчер (частый тик) + уборщик (ежедневно) ----
            q.AddJob<OutboxDispatchJob>(j => j
                .WithIdentity(OutboxDispatchJob.Key)
                .StoreDurably()
                .WithDescription("Доставка сообщений outbox (email/Telegram/хранилище)"));

            q.AddTrigger(t => t
                .ForJob(OutboxDispatchJob.Key)
                .WithIdentity("outbox-dispatch-interval")
                .WithSimpleSchedule(x => x
                    .WithIntervalInSeconds(dispatchInterval)
                    .RepeatForever()
                    // При пропуске (узел был занят/выключен) не копим отставшие тики — один следующий.
                    .WithMisfireHandlingInstructionNextWithRemainingCount()));

            q.AddJob<OutboxCleanupJob>(j => j
                .WithIdentity(OutboxCleanupJob.Key)
                .StoreDurably()
                .WithDescription("Удаление доставленных сообщений outbox старше срока хранения"));

            q.AddTrigger(t => t
                .ForJob(OutboxCleanupJob.Key)
                .WithIdentity("outbox-cleanup-daily")
                .WithCronSchedule(outbox.CleanupCron, x => x.WithMisfireHandlingInstructionDoNothing()));

            // ---- Сохранённые поиски: рассылка новых совпадений (по умолчанию раз в 15 минут) ----
            q.AddJob<SavedSearchNotificationJob>(j => j
                .WithIdentity(SavedSearchNotificationJob.Key)
                .StoreDurably()
                .WithDescription("Поиск новых объявлений по сохранённым поискам и уведомление авторов"));

            q.AddTrigger(t => t
                .ForJob(SavedSearchNotificationJob.Key)
                .WithIdentity("saved-search-notification-interval")
                .WithCronSchedule(savedSearchCron, x => x.WithMisfireHandlingInstructionDoNothing()));
        });

        // WaitForJobsToComplete — не рвём выполнение джоба при остановке приложения.
        services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);

        return services;
    }

    private const string SchedulingSection = "Scheduling";
}
