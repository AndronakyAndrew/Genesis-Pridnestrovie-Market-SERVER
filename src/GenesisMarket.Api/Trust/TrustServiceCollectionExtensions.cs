namespace GenesisMarket.Api.Trust;

public static class TrustServiceCollectionExtensions
{
    /// <summary>Слой доверия: отзывы (репутация) и жалобы (модерация).</summary>
    public static IServiceCollection AddTrustFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<TrustOptions>(configuration.GetSection(TrustOptions.Section));
        // In-memory счётчики живут между запросами ⇒ singleton (как auth rate-limiter).
        services.AddSingleton<IReportRateLimiter, ReportRateLimiter>();
        return services;
    }
}
