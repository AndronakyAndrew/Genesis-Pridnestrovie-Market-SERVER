using FluentValidation;
using GenesisMarket.Api.Invitations;
using GenesisMarket.Infrastructure.Invitations;

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

        // Справочники каталога из БД (города, категории, подкатегории) — кэшируемый снимок.
        services.AddMemoryCache();
        services.AddScoped<ICatalogReferences, CatalogReferences>();
        // Политика в БД не ходит и состояния не держит — синглтона достаточно.
        services.AddSingleton<IListingModerationPolicy, ListingModerationPolicy>();
        services.AddScoped<IListingViewCounter, ListingViewCounter>();
        services.AddScoped<IListingLifecycleService, ListingLifecycleService>();
        services.AddScoped<IContactRevealService, ContactRevealService>();
        services.AddScoped<ILinkRedirectService, LinkRedirectService>();
        services.AddValidatorsFromAssemblyContaining<CreateListingRequestValidator>();

        // Приглашения продавцов (опции читает и джоб очистки — секция одна и та же).
        services.Configure<InvitationOptions>(configuration.GetSection(InvitationOptions.Section));
        services.AddScoped<InvitationService>();
        services.AddSingleton<InvitationPhotoUrls>();
        return services;
    }
}
