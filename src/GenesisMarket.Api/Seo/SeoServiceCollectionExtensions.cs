namespace GenesisMarket.Api.Seo;

public static class SeoServiceCollectionExtensions
{
    /// <summary>
    /// Индексация/SEO: мета карточек, карта сайта, robots, посадочные.
    /// Публичный адрес сайта берётся из <c>Seo:WebBaseUrl</c> (переменная
    /// <c>Seo__WebBaseUrl</c>); если он не задан — из <c>SITE_BASE_URL</c>.
    /// </summary>
    public static IServiceCollection AddSeoFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<SeoOptions>(configuration.GetSection(SeoOptions.Section));

        // Алиас для деплоя: домен площадки одной переменной SITE_BASE_URL, без двойного
        // подчёркивания. Приоритет у Seo:WebBaseUrl — алиас только заполняет пустое место.
        services.PostConfigure<SeoOptions>(o =>
        {
            if (string.IsNullOrWhiteSpace(o.WebBaseUrl))
                o.WebBaseUrl = configuration["SITE_BASE_URL"] ?? "";
        });

        services.AddScoped<ISitemapUrlProvider, SitemapUrlProvider>();
        services.AddSingleton<SitemapCache>();
        return services;
    }
}
