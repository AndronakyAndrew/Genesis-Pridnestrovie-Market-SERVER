namespace GenesisMarket.Api.Auth;

/// <summary>Отправка SMS. Код подтверждения телефона шлётся напрямую от Genesis Market.</summary>
public interface ISmsSender
{
    Task SendAsync(string phoneE164, string message, CancellationToken ct);
}

/// <summary>
/// Канал доставки кода не сконфигурирован. Отдельный тип, чтобы отличить это от
/// сбоя провайдера: пользователю отвечаем 503, а не 500.
/// </summary>
public sealed class VerificationChannelUnavailableException(string message) : Exception(message);

/// <summary>
/// Dev-реализация: не отправляет реальную SMS, а пишет её в лог (Warning),
/// чтобы код был виден при локальной разработке.
/// Регистрируется ТОЛЬКО в Development (см. <c>AddGenesisAuth</c>): в логе
/// оказываются и номер телефона, и код подтверждения — вне разработки это
/// одновременно утечка персональных данных и путь к захвату аккаунта.
/// </summary>
public sealed class DevSmsSender(ILogger<DevSmsSender> logger) : ISmsSender
{
    public Task SendAsync(string phoneE164, string message, CancellationToken ct)
    {
        logger.LogWarning("[DEV SMS] Кому: {Phone} | Текст: {Message}", phoneE164, message);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Реализация по умолчанию вне Development: SMS-провайдера в проекте пока нет.
/// Честно сообщает, что канал недоступен, вместо записи кода и номера в лог.
/// Заменяется настоящим провайдером через ту же абстракцию <see cref="ISmsSender"/>.
/// </summary>
public sealed class UnavailableSmsSender : ISmsSender
{
    public Task SendAsync(string phoneE164, string message, CancellationToken ct) =>
        throw new VerificationChannelUnavailableException(
            "SMS-провайдер не сконфигурирован: подтверждение телефона недоступно.");
}
