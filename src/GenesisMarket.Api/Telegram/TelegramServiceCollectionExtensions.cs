namespace GenesisMarket.Api.Telegram;

public static class TelegramServiceCollectionExtensions
{
    /// <summary>
    /// Служебный бот (long polling). Регистрируется всегда: без токена воркер пишет warning
    /// и завершается, остальное приложение этого не замечает.
    /// </summary>
    public static IServiceCollection AddTelegram(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<TelegramOptions>(configuration.GetSection(TelegramOptions.SectionName));

        // Имя клиента явное. По умолчанию оно берётся из имени интерфейса без namespace —
        // "ITelegramClient", а outbox регистрирует свой Outbox.Telegram.ITelegramClient под тем же
        // именем; фабрика не даёт связать одно имя с двумя типами и падает при резолве.
        services.AddHttpClient<ITelegramClient, TelegramClient>("TelegramBot", c =>
            {
                // Long polling держит соединение 30 секунд — таймаут должен быть с запасом.
                c.Timeout = TimeSpan.FromSeconds(60);
            })
            // Токен — часть пути (/bot<token>/method), а стандартные логгеры фабрики пишут URL
            // на Information: без этого токен оседал бы в логах на каждом getUpdates.
            .RemoveAllLoggers()
            // Клиент живёт в singleton-воркере, ротация handler'а фабрикой до него не доходит —
            // соединения пересоздаются сами, чтобы подхватывать смену DNS.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });

        services.AddHostedService<TelegramUpdateWorker>();

        return services;
    }
}
