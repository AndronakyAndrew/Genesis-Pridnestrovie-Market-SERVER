using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Telegram.Channel;

/// <summary>
/// Раз в <see cref="ChannelPublishingOptions.PollIntervalSeconds"/> публикует самое старое
/// одобренное объявление из очереди — в рабочем окне и не чаще
/// <see cref="ChannelPublishingOptions.MinIntervalMinutes"/>. Вся логика тика — в
/// <see cref="IChannelPublisher.PublishNextAsync"/>; здесь расписание и живучесть.
/// </summary>
/// <remarks>
/// Как и бот, публикация вторична: без токена/канала/часового пояса воркер пишет в лог и
/// завершается, ошибка тика не выходит наружу (необработанное исключение BackgroundService
/// остановило бы весь хост). Рассчитан на один инстанс API.
/// </remarks>
public sealed class ChannelPublisherWorker(
    IServiceScopeFactory scopes,
    IOptions<ChannelPublishingOptions> options,
    IOptions<TelegramOptions> telegramOptions,
    TimeProvider time,
    ILogger<ChannelPublisherWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await RunAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Штатная остановка приложения.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Публикация в Telegram-канал остановлена из-за ошибки. API продолжает работать.");
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var o = options.Value;
        var tg = telegramOptions.Value;

        if (!o.Enabled)
        {
            logger.LogInformation("Публикация в Telegram-канал выключена (ChannelPublishing:Enabled).");
            return;
        }

        if (tg.BotTokenProblem() is { } tokenProblem)
        {
            logger.LogWarning(
                "Публикация в Telegram-канал не запущена: {Problem} Одобренные объявления копятся в очереди.",
                tokenProblem);
            return;
        }

        if (string.IsNullOrWhiteSpace(tg.ChannelId))
        {
            logger.LogWarning(
                "Публикация в Telegram-канал не запущена: Telegram:ChannelId не задан (TELEGRAM_CHANNEL_ID). " +
                "Одобренные объявления копятся в очереди.");
            return;
        }

        if (ChannelSchedule.ResolveTimeZone(o.TimeZoneId) is null)
        {
            logger.LogError(
                "Публикация в Telegram-канал не запущена: часовой пояс '{TimeZone}' не найден " +
                "(ChannelPublishing:TimeZoneId; в контейнере нужен пакет tzdata).", o.TimeZoneId);
            return;
        }

        logger.LogInformation(
            "Публикация в Telegram-канал запущена: окно {Start}–{End} ({TimeZone}), не чаще раза в {Interval} мин.",
            o.WindowStart, o.WindowEnd, o.TimeZoneId, o.MinIntervalMinutes);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, o.PollIntervalSeconds)), time);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var publisher = scope.ServiceProvider.GetRequiredService<IChannelPublisher>();
                var outcome = await publisher.PublishNextAsync(time.GetUtcNow(), ct);
                logger.LogDebug("Тик публикации в канал: {Outcome}", outcome);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // БД недоступна, битая конфигурация — пробуем снова на следующем тике.
                logger.LogError(ex, "Ошибка тика публикации в Telegram-канал");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}
