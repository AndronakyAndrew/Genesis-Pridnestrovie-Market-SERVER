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
        services.Configure<ContactRevealOptions>(configuration.GetSection(ContactRevealOptions.Section));
        services.AddScoped<IListingModerationPolicy, ListingModerationPolicy>();
        services.AddScoped<IListingViewCounter, ListingViewCounter>();
        services.AddScoped<IContactRevealService, ContactRevealService>();
        services.AddValidatorsFromAssemblyContaining<CreateListingRequestValidator>();
        return services;
    }
}
