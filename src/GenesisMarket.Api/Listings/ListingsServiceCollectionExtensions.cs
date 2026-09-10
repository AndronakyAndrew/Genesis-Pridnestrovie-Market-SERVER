using FluentValidation;

namespace GenesisMarket.Api.Listings;

public static class ListingsServiceCollectionExtensions
{
    /// <summary>Жизненный цикл объявлений: пороги, премодерация, просмотры, валидаторы.</summary>
    public static IServiceCollection AddListingsFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ListingOptions>(configuration.GetSection(ListingOptions.Section));
        services.Configure<ModerationOptions>(configuration.GetSection(ModerationOptions.Section));
        services.Configure<ContactRevealOptions>(configuration.GetSection(ContactRevealOptions.Section));
        // Политика в БД не ходит и состояния не держит — синглтона достаточно.
        services.AddSingleton<IListingModerationPolicy, ListingModerationPolicy>();
        services.AddScoped<IListingViewCounter, ListingViewCounter>();
        services.AddScoped<IContactRevealService, ContactRevealService>();
        services.AddValidatorsFromAssemblyContaining<CreateListingRequestValidator>();
        return services;
    }
}
