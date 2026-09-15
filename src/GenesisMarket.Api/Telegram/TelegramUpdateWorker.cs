using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Telegram;

/// <summary>
/// Long polling. Обращения из личек пользователей уходят админу,
/// ответ админа (реплаем на пересланное) возвращается пользователю.
/// Связь «кому отвечать» зашита в текст служебного сообщения (uid:123),
/// поэтому переживает рестарт сервиса — состояние в памяти не хранится.
/// </summary>
/// <remarks>
/// Бот вторичен: площадка обязана работать без него. Любая проблема конфигурации или сети
/// заканчивается warning/error в логе и выходом из воркера, но не исключением наружу —
/// необработанное исключение BackgroundService по умолчанию останавливает весь хост
/// (<see cref="BackgroundServiceExceptionBehavior.StopHost"/>).
/// </remarks>
public sealed class TelegramUpdateWorker : BackgroundService
{
    private static readonly Regex UidRegex = new(@"\(uid:(\d+)\)", RegexOptions.Compiled);

    // 409: getUpdates забирает кто-то ещё (второй инстанс или выставленный webhook).
    private static readonly TimeSpan ConflictDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxFailureDelay = TimeSpan.FromMinutes(5);

    private readonly ITelegramClient _tg;
    private readonly IOptions<TelegramOptions> _options;
    private readonly ILogger<TelegramUpdateWorker> _log;
    private TelegramOptions _opt = new();
    private long _offset;

    public TelegramUpdateWorker(ITelegramClient tg, IOptions<TelegramOptions> opt,
        ILogger<TelegramUpdateWorker> log)
    {
        _tg = tg;
        _options = opt;
        _log = log;
    }

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
            _log.LogError(ex, "Telegram-бот остановлен из-за ошибки. API продолжает работать.");
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Биндинг может бросить на мусорном значении (Telegram__AdminChatId=@name) — это ловит ExecuteAsync.
        _opt = _options.Value;

        if (!_opt.EnableUpdatePolling)
        {
            _log.LogInformation("Telegram polling выключен.");
            return;
        }

        if (_opt.BotTokenProblem() is { } tokenProblem)
        {
            _log.LogWarning("Telegram-бот не запущен: {Problem} Площадка работает без него.", tokenProblem);
            return;
        }

        if (_opt.AdminChatId == 0)
        {
            _log.LogWarning(
                "Telegram-бот не запущен: Telegram:AdminChatId не задан (переменная TELEGRAM_ADMIN_CHAT_ID), " +
                "обращения пользователей некуда пересылать. Площадка работает без него.");
            return;
        }

        var botChecked = false;
        var failures = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Токен проверяем до первого getUpdates: с отклонённым токеном polling бессмыслен.
                if (!botChecked)
                {
                    var me = await _tg.GetMeAsync(ct);
                    botChecked = true;
                    _log.LogInformation("Telegram polling запущен: @{BotUsername}.", me.Username);
                }

                var updates = await _tg.CallAsync("getUpdates", new
                {
                    offset = _offset,
                    timeout = 30,
                    allowed_updates = new[] { "message" }
                }, ct);

                failures = 0;

                foreach (var update in updates.EnumerateArray())
                {
                    _offset = update.GetProperty("update_id").GetInt64() + 1;

                    if (!update.TryGetProperty("message", out var msg))
                        continue;

                    try
                    {
                        await HandleMessageAsync(msg, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogError(ex, "Ошибка обработки сообщения Telegram");
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (TelegramApiException ex) when (ex.IsTokenRejected)
            {
                // Отозванный или опечатанный токен сам не починится — не крутим цикл впустую.
                _log.LogWarning(
                    "Telegram отклонил токен бота (код {Code}), polling остановлен. " +
                    "Проверьте TELEGRAM_BOT_TOKEN; площадка работает без бота.", ex.ErrorCode);
                return;
            }
            catch (TelegramApiException ex) when (ex.ErrorCode == 409)
            {
                _log.LogWarning(
                    "Telegram 409: обновления забирает другой инстанс или выставлен webhook ({Reason}). " +
                    "Повтор через {Delay}s.", ex.Message, ConflictDelay.TotalSeconds);
                await Task.Delay(ConflictDelay, ct);
            }
            catch (Exception ex)
            {
                // Сеть/таймауты: пауза растёт до 5 минут, чтобы недоступный Telegram не забивал лог.
                failures++;
                var delay = TimeSpan.FromSeconds(
                    Math.Min(MaxFailureDelay.TotalSeconds, 5 * Math.Pow(2, Math.Min(failures - 1, 10))));
                _log.LogError(ex, "Ошибка long polling, пауза {Delay}s", delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    private async Task HandleMessageAsync(JsonElement msg, CancellationToken ct)
    {
        var chat = msg.GetProperty("chat");
        var chatType = chat.GetProperty("type").GetString();
        var chatId = chat.GetProperty("id").GetInt64();

        // Посты самого канала и групповые чаты игнорируем.
        if (chatType != "private")
            return;

        var text = msg.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";

        if (chatId == _opt.AdminChatId)
        {
            await HandleAdminMessageAsync(msg, text, ct);
            return;
        }

        if (text.StartsWith("/start", StringComparison.Ordinal))
        {
            await _tg.SendMessageAsync(chatId.ToString(),
                "Это служебный бот «Местной площадки Genesis».\n\n" +
                "Напишите сюда, если заметили ошибку, неудобство или у вас есть предложение — " +
                "сообщение дойдёт напрямую до разработчика.\n\n" +
                $"Сама площадка: {_opt.SiteBaseUrl}", ct: ct);
            return;
        }

        await ForwardToAdminAsync(msg, chatId, text, ct);
    }

    private async Task ForwardToAdminAsync(JsonElement msg, long fromChatId, string text,
        CancellationToken ct)
    {
        var from = msg.GetProperty("from");
        var name = from.TryGetProperty("first_name", out var f) ? f.GetString() ?? "" : "";
        var username = from.TryGetProperty("username", out var u) ? u.GetString() : null;
        var label = username is null ? name : $"{name} @{username}";

        var admin = _opt.AdminChatId.ToString();

        if (string.IsNullOrEmpty(text))
        {
            // Медиа и прочее пересылаем как есть — ответить можно вручную.
            await _tg.CallAsync("forwardMessage", new
            {
                chat_id = admin,
                from_chat_id = fromChatId,
                message_id = msg.GetProperty("message_id").GetInt32()
            }, ct);

            await _tg.SendMessageAsync(admin,
                $"↑ вложение от {WebUtility.HtmlEncode(label)} (uid:{fromChatId})", ct: ct);
        }
        else
        {
            await _tg.SendMessageAsync(admin,
                $"💬 <b>{WebUtility.HtmlEncode(label)}</b> (uid:{fromChatId})\n\n" +
                WebUtility.HtmlEncode(text) +
                "\n\n<i>Ответьте реплаем на это сообщение — оно уйдёт пользователю.</i>", ct: ct);
        }

        await _tg.SendMessageAsync(fromChatId.ToString(),
            "Спасибо, сообщение получено. Ответим здесь же.", ct: ct);
    }

    private async Task HandleAdminMessageAsync(JsonElement msg, string text, CancellationToken ct)
    {
        if (!msg.TryGetProperty("reply_to_message", out var reply))
            return;

        var quoted = reply.TryGetProperty("text", out var q) ? q.GetString() ?? "" : "";
        var match = UidRegex.Match(quoted);

        if (!match.Success)
        {
            await _tg.SendMessageAsync(_opt.AdminChatId.ToString(),
                "Не вижу uid в сообщении, на которое отвечаешь. " +
                "Реплаить нужно на служебное сообщение бота, а не на пересланное вложение.", ct: ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
            return;

        var userId = match.Groups[1].Value;

        try
        {
            await _tg.SendMessageAsync(userId, WebUtility.HtmlEncode(text), ct: ct);
            await _tg.SendMessageAsync(_opt.AdminChatId.ToString(), "✅ Отправлено", ct: ct);
        }
        catch (TelegramApiException ex)
        {
            await _tg.SendMessageAsync(_opt.AdminChatId.ToString(),
                $"❌ Не доставлено: {WebUtility.HtmlEncode(ex.Message)}", ct: ct);
        }
    }
}
