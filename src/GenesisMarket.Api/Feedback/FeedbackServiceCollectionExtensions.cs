namespace GenesisMarket.Api.Feedback;

public static class FeedbackServiceCollectionExtensions
{
    /// <summary>
    /// Форма обратной связи: отправка email-уведомлений через Resend. Без ключа реальную сеть
    /// не трогаем — пишем в лог (как <see cref="Auth.LogEmailSender"/> для SMTP). Сам приём
    /// обращения (<c>POST /api/feedback</c>) и доставка письма (обработчик outbox) регистрируются
    /// отдельно — контроллер сам по себе не имеет зависимостей, а обработчик выбирается
    /// диспетчером по типу сообщения (см. <c>AddOutbox</c>).
    /// </summary>
    public static IServiceCollection AddFeedbackFeature(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ResendOptions>(configuration.GetSection(ResendOptions.Section));

        if (string.IsNullOrWhiteSpace(configuration["Resend:ApiKey"]))
        {
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
