namespace GenesisMarket.Api.Trust;

public static class TrustServiceCollectionExtensions
{
    /// <summary>Слой доверия: отзывы (репутация) и жалобы (модерация).</summary>
    public static IServiceCollection AddTrustFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<TrustOptions>(configuration.GetSection(TrustOptions.Section));
        // Rate-limit жалоб — на встроенном RateLimiter (политика "report", лимиты из TrustOptions).
        return services;
    }
}
