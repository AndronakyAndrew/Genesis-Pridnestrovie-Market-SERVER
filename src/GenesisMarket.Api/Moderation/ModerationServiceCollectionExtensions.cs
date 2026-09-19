namespace GenesisMarket.Api.Moderation;

public static class ModerationServiceCollectionExtensions
{
    /// <summary>Инструменты модератора: очередь, карточки, действия и аудит-журнал.</summary>
    public static IServiceCollection AddModerationFeature(this IServiceCollection services)
    {
        // Аудит пишет в текущий DbContext (scoped) от имени текущего пользователя.
        services.AddScoped<IModerationAudit, ModerationAudit>();

        // Решения по объявлениям — одно место для одиночных и пакетных ручек.
        services.AddScoped<IListingDecisions, ListingDecisions>();
        return services;
    }
}
