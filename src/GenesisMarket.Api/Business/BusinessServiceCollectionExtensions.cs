using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GenesisMarket.Api.Business;

public static class BusinessServiceCollectionExtensions
{
    /// <summary>
    /// Бизнес-режим: реквизиты, подача на проверку, решения модератора.
    /// Валидатор запроса регистрируется сканированием сборки в AddListingsFeature;
    /// лимит частоты подачи — политика <c>business-submit</c> в RateLimitingSetup.
    /// </summary>
    public static IServiceCollection AddBusinessFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<BusinessOptions>(configuration.GetSection(BusinessOptions.Section));
        services.TryAddSingleton(TimeProvider.System);

        // Шаблон номера компилируется один раз на процесс.
        services.AddSingleton<RegistrationNumberRule>();

        services.AddScoped<IBusinessAccountService, BusinessAccountService>();
        services.AddScoped<IBusinessVerificationService, BusinessVerificationService>();
        return services;
    }
}
