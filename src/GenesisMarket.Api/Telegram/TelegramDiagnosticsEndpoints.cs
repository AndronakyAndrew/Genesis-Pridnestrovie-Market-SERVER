using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Telegram;

/// <summary>
/// Дымовая проверка конфигурации бота: вызывает getMe и отдаёт имя бота, ничего не отправляя.
/// Маппится только в Development (см. Program.cs) — в остальных окружениях маршрута нет вовсе.
/// </summary>
public static class TelegramDiagnosticsEndpoints
{
    public const string Route = "/api/dev/telegram/me";

    public static IEndpointConventionBuilder MapTelegramDiagnostics(this IEndpointRouteBuilder app) =>
        app.MapGet(Route, GetMeAsync)
            // Dev-only и отдаёт только публичные данные бота — JWT для проверки конфигурации не нужен.
            .AllowAnonymous()
            .ExcludeFromDescription();

    private static async Task<IResult> GetMeAsync(
        ITelegramClient telegram, IOptions<TelegramOptions> options, CancellationToken ct)
    {
        TelegramOptions opt;
        try
        {
            opt = options.Value;
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "Секция Telegram не читается", detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (opt.BotTokenProblem() is { } problem)
            return Results.Problem(title: "Telegram-бот не настроен", detail: problem,
                statusCode: StatusCodes.Status503ServiceUnavailable);

        try
        {
            var me = await telegram.GetMeAsync(ct);
            return Results.Ok(new TelegramBotStatus(
                me.Id,
                me.Username,
                me.FirstName,
                AdminChatIdSet: opt.AdminChatId != 0,
                ChannelIdSet: !string.IsNullOrWhiteSpace(opt.ChannelId),
                UpdatePollingEnabled: opt.EnableUpdatePolling));
        }
        catch (TelegramApiException ex) when (ex.IsTokenRejected)
        {
            return Results.Problem(title: "Telegram отклонил токен бота", detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (TelegramApiException ex)
        {
            return Results.Problem(title: "Telegram вернул ошибку", detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (HttpRequestException ex)
        {
            return Results.Problem(title: "Telegram недоступен", detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Results.Problem(title: "Telegram не ответил вовремя",
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }
}

public sealed record TelegramBotStatus(
    long Id,
    string Username,
    string FirstName,
    bool AdminChatIdSet,
    bool ChannelIdSet,
    bool UpdatePollingEnabled);
