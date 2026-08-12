using GenesisMarket.Infrastructure.Scheduling;

namespace GenesisMarket.Api.SavedSearches;

public static class SavedSearchServiceCollectionExtensions
{
    /// <summary>
    /// Сохранённые поиски: сервис рассылки (реализация интерфейса из Infrastructure, который
    /// дёргает Quartz-джоб). Опции секции <c>SavedSearch</c> регистрирует <c>AddScheduling</c>;
    /// в тестах планировщик выключен, а сервис прогоняют напрямую.
    /// </summary>
    public static IServiceCollection AddSavedSearchesFeature(this IServiceCollection services)
    {
        services.AddScoped<ISavedSearchNotificationService, SavedSearchNotificationService>();
        return services;
    }
}
