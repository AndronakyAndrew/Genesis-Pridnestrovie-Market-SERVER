using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GenesisMarket.Api.Telegram.Channel;

public static class ChannelPublishingServiceCollectionExtensions
{
    /// <summary>
    /// Очередь публикации в Telegram-канал и её воркер. Клиент Bot API регистрирует
    /// <see cref="TelegramServiceCollectionExtensions.AddTelegram"/>.
    /// </summary>
    public static IServiceCollection AddChannelPublishing(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ChannelPublishingOptions>(configuration.GetSection(ChannelPublishingOptions.Section));
        services.TryAddSingleton(TimeProvider.System);

        // Scoped — работает с DbContext запроса/тика; из outbox вызывается в транзакции диспетчера.
        services.AddScoped<IChannelPublisher, ChannelPublisher>();
        services.AddHostedService<ChannelPublisherWorker>();

        return services;
    }
}
