using GenesisMarket.Infrastructure.Outbox;

namespace GenesisMarket.Api.Outbox;

public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Доставка транзакционного outbox: диспетчер, каналы (email/Telegram) и обработчики
    /// по типам. Расписание/тик задаёт Quartz (см. <c>AddScheduling</c>); здесь — сама доставка.
    /// В тестах планировщик выключен, а <see cref="IOutboxDispatcher"/> прогоняют напрямую.
    /// </summary>
    public static IServiceCollection AddOutbox(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<TelegramOptions>(configuration.GetSection(TelegramOptions.Section));

        // Диспетчер и адресная доставка пользователю — scoped (нужен DbContext).
        services.AddScoped<IOutboxDispatcher, OutboxDispatcher>();
        services.AddScoped<IUserNotifier, UserNotifier>();

        // Каналы: почта — поверх уже настроенного IEmailSender; Telegram — HTTP или dev-лог.
        services.AddSingleton<INotificationChannel, EmailNotificationChannel>();
        services.AddSingleton<INotificationChannel, TelegramNotificationChannel>();

        // Без токена бота реальную сеть не трогаем — пишем в лог (как LogEmailSender для почты).
        if (string.IsNullOrWhiteSpace(configuration["Telegram:BotToken"]))
            services.AddSingleton<ITelegramClient, LogTelegramClient>();
        else
            services.AddHttpClient<ITelegramClient, HttpTelegramClient>();

        // Обработчики типов сообщений. Scoped — читают БД.
        services.AddScoped<IOutboxHandler, ListingApprovedHandler>();
        services.AddScoped<IOutboxHandler, ListingRejectedHandler>();
        services.AddScoped<IOutboxHandler, ListingExpiringSoonHandler>();
        services.AddScoped<IOutboxHandler, NewReviewHandler>();
        services.AddScoped<IOutboxHandler, ListingPublishedHandler>();
        services.AddScoped<IOutboxHandler, DeleteImagesHandler>();
        services.AddScoped<IOutboxHandler, DeleteObjectHandler>();

        return services;
    }
}
