namespace GenesisMarket.Api.Auth;

/// <summary>Отправка SMS. Код подтверждения телефона шлётся напрямую от Genesis Market.</summary>
public interface ISmsSender
{
    Task SendAsync(string phoneE164, string message, CancellationToken ct);
}

/// <summary>
/// Dev-реализация: не отправляет реальную SMS, а пишет её в лог (Warning),
/// чтобы код был виден при локальной разработке. В проде заменяется реальным
/// SMS-провайдером (та же абстракция <see cref="ISmsSender"/>).
/// </summary>
public sealed class DevSmsSender(ILogger<DevSmsSender> logger) : ISmsSender
{
    public Task SendAsync(string phoneE164, string message, CancellationToken ct)
    {
        logger.LogWarning("[DEV SMS] Кому: {Phone} | Текст: {Message}", phoneE164, message);
        return Task.CompletedTask;
    }
}
