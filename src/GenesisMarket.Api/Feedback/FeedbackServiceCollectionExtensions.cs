namespace GenesisMarket.Api.Feedback;

public static class FeedbackServiceCollectionExtensions
{
    /// <summary>
    /// Форма обратной связи: отправка email-уведомлений через Resend. В разработке без ключа
    /// реальную сеть не трогаем — пишем в лог (как <see cref="Auth.LogEmailSender"/> для SMTP).
    /// Вне Development лог-заглушка запрещена: она печатает имя, контакт и текст обращения
    /// открытым текстом, то есть превращает журнал контейнера в хранилище персональных данных.
    /// Сам приём обращения (<c>POST /api/feedback</c>) и доставка письма (обработчик outbox)
    /// регистрируются отдельно — контроллер сам по себе не имеет зависимостей, а обработчик
    /// выбирается диспетчером по типу сообщения (см. <c>AddOutbox</c>).
    /// </summary>
    public static IServiceCollection AddFeedbackFeature(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<ResendOptions>(configuration.GetSection(ResendOptions.Section));

        if (string.IsNullOrWhiteSpace(configuration["Resend:ApiKey"]))
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException(
                    $"Resend:ApiKey не задан, а окружение — {environment.EnvironmentName}. " +
                    "Без ключа обращения из формы обратной связи писались бы в лог вместе " +
                    "с контактами отправителей. Задайте RESEND_API_KEY в окружении.");

            services.AddSingleton<IResendEmailService, LogResendEmailService>();
        }
        else
        {
            services.AddHttpClient<IResendEmailService, ResendEmailService>(http =>
            {
                http.BaseAddress = new Uri("https://api.resend.com/");
                http.Timeout = TimeSpan.FromSeconds(5);
            });
        }

        return services;
    }
}
