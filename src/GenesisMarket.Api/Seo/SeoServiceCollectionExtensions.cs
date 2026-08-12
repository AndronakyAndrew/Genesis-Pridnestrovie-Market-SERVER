namespace GenesisMarket.Api.Seo;

public static class SeoServiceCollectionExtensions
{
    /// <summary>Индексация/SEO: мета карточек, sitemap, robots, посадочные (только конфигурация).</summary>
    public static IServiceCollection AddSeoFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<SeoOptions>(configuration.GetSection(SeoOptions.Section));
        return services;
    }
}
