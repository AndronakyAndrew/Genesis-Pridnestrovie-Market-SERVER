using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Telegram;

public interface ITelegramClient
{
    /// <summary>getMe: кто этот бот. Ничего не отправляет — годится для проверки токена.</summary>
    Task<TelegramBotInfo> GetMeAsync(CancellationToken ct = default);

    Task<int> SendMessageAsync(string chatId, string text, InlineButton? button = null,
        bool disablePreview = true, CancellationToken ct = default);

    Task<int> SendPhotoAsync(string chatId, string photoUrl, string caption,
        InlineButton? button = null, CancellationToken ct = default);

    Task EditCaptionAsync(string chatId, int messageId, string caption,
        InlineButton? button = null, CancellationToken ct = default);

    Task DeleteMessageAsync(string chatId, int messageId, CancellationToken ct = default);

    Task<JsonElement> CallAsync(string method, object? payload, CancellationToken ct = default);
}

public sealed record InlineButton(string Text, string Url);

public sealed record TelegramBotInfo(long Id, string Username, string FirstName);

public sealed class TelegramApiException : Exception
{
    public int ErrorCode { get; }

    public TelegramApiException(int code, string description)
        : base($"Telegram API {code}: {description}") => ErrorCode = code;

    /// <summary>401 — токен отозван или неверен, 404 — путь /bot&lt;token&gt;/ не распознан.</summary>
    public bool IsTokenRejected => ErrorCode is 401 or 404;
}

public sealed class TelegramClient : ITelegramClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly IOptions<TelegramOptions> _options;
    private readonly ILogger<TelegramClient> _log;

    public TelegramClient(HttpClient http, IOptions<TelegramOptions> options, ILogger<TelegramClient> log)
    {
        _http = http;
        // Value читаем при вызове, а не здесь: мусор в Telegram__AdminChatId ломает биндинг,
        // и исключение из конструктора уронило бы создание воркера, а с ним и старт хоста.
        _options = options;
        _log = log;
    }

    public async Task<JsonElement> CallAsync(string method, object? payload, CancellationToken ct = default)
    {
        var opt = _options.Value;

        // Без токена в сеть не ходим: запрос на /bot/getMe всё равно получил бы 404.
        if (opt.BotTokenProblem() is { } problem)
            throw new InvalidOperationException(problem);

        // URL содержит токен — в логи и исключения он не попадает (логгеры фабрики сняты,
        // см. TelegramServiceCollectionExtensions).
        var url = $"https://api.telegram.org/bot{opt.BotToken}/{method}";

        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (payload is not null)
            {
                req.Content = new StringContent(
                    JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");
            }

            using var res = await _http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                // Не JSON (HTML-заглушка прокси, обрыв) — судим по HTTP-статусу.
                throw new TelegramApiException((int)res.StatusCode, "ответ не JSON");
            }

            using (doc)
            {
                var root = doc.RootElement;

                if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                    return root.GetProperty("result").Clone();

                var code = root.TryGetProperty("error_code", out var c) ? c.GetInt32() : (int)res.StatusCode;
                var desc = root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";

                // 429: Telegram сам говорит, сколько ждать. Не долбим.
                if (code == 429 &&
                    root.TryGetProperty("parameters", out var p) &&
                    p.TryGetProperty("retry_after", out var ra))
                {
                    var wait = TimeSpan.FromSeconds(ra.GetInt32() + 1);
                    _log.LogWarning("Telegram 429 на {Method}, ждём {Wait}s", method, wait.TotalSeconds);
                    await Task.Delay(wait, ct);
                    continue;
                }

                // Временные сбои на стороне Telegram — до трёх попыток.
                if (code >= 500 && attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                    continue;
                }

                throw new TelegramApiException(code, desc);
            }
        }
    }

    public async Task<TelegramBotInfo> GetMeAsync(CancellationToken ct = default)
    {
        var result = await CallAsync("getMe", null, ct);

        return new TelegramBotInfo(
            result.GetProperty("id").GetInt64(),
            result.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "",
            result.TryGetProperty("first_name", out var f) ? f.GetString() ?? "" : "");
    }

    public async Task<int> SendMessageAsync(string chatId, string text, InlineButton? button = null,
        bool disablePreview = true, CancellationToken ct = default)
    {
        var result = await CallAsync("sendMessage", new
        {
            chat_id = chatId,
            text,
            parse_mode = "HTML",
            link_preview_options = new { is_disabled = disablePreview },
            reply_markup = Markup(button)
        }, ct);

        return result.GetProperty("message_id").GetInt32();
    }

    public async Task<int> SendPhotoAsync(string chatId, string photoUrl, string caption,
        InlineButton? button = null, CancellationToken ct = default)
    {
        var result = await CallAsync("sendPhoto", new
        {
            chat_id = chatId,
            photo = photoUrl,
            caption,
            parse_mode = "HTML",
            reply_markup = Markup(button)
        }, ct);

        return result.GetProperty("message_id").GetInt32();
    }

    public Task EditCaptionAsync(string chatId, int messageId, string caption,
        InlineButton? button = null, CancellationToken ct = default)
        => CallAsync("editMessageCaption", new
        {
            chat_id = chatId,
            message_id = messageId,
            caption,
            parse_mode = "HTML",
            // Без кнопки — явно пустая клавиатура: так кнопка гарантированно снимается.
            // Что Telegram делает с клавиатурой при отсутствии reply_markup, документация не говорит.
            reply_markup = Markup(button) ?? EmptyKeyboard
        }, ct);

    private static readonly object EmptyKeyboard = new { inline_keyboard = Array.Empty<object>() };

    public Task DeleteMessageAsync(string chatId, int messageId, CancellationToken ct = default)
        => CallAsync("deleteMessage", new { chat_id = chatId, message_id = messageId }, ct);

    private static object? Markup(InlineButton? b) => b is null
        ? null
        : new { inline_keyboard = new[] { new[] { new { text = b.Text, url = b.Url } } } };
}
