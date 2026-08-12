using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GenesisMarket.Domain.Enums;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace GenesisMarket.Api.Outbox.Telegram;

/// <summary>
/// Боевой клиент Telegram Bot API поверх <see cref="HttpClient"/>. Разбирает конверт ответа
/// (<c>ok</c>/<c>error_code</c>/<c>description</c>/<c>parameters.retry_after</c>) и транслирует
/// его в типизированные исключения. Ретраи 429 и временных сбоев — через Polly (задержка при
/// 429 берётся из <c>retry_after</c> тела ответа). Частоту в один канал заранее ограничивает
/// <see cref="ITelegramRateLimiter"/>, чтобы не упираться в 429 на нормальной нагрузке.
/// </summary>
public sealed class HttpTelegramClient : ITelegramClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly TelegramOptions _o;
    private readonly ITelegramRateLimiter _limiter;
    private readonly ILogger<HttpTelegramClient> _logger;
    private readonly AsyncRetryPolicy _retry;

    public HttpTelegramClient(
        HttpClient http,
        IOptions<TelegramOptions> options,
        ITelegramRateLimiter limiter,
        ILogger<HttpTelegramClient> logger)
    {
        _http = http;
        _o = options.Value;
        _limiter = limiter;
        _logger = logger;

        // Ретраи только транзиентного: 429 (задержка из retry_after) и сеть/5xx (экспонента).
        // Постоянные 4xx (TelegramApiException) не ретраятся — их обрабатывает вызывающий.
        _retry = Policy
            .Handle<TelegramRateLimitException>()
            .Or<TelegramTransientException>()
            .WaitAndRetryAsync(
                retryCount: 4,
                sleepDurationProvider: (attempt, exception, _) => exception is TelegramRateLimitException rl
                    ? rl.RetryAfter + TimeSpan.FromMilliseconds(250) // небольшой запас поверх retry_after
                    : TimeSpan.FromSeconds(Math.Pow(2, attempt)),    // 2s, 4s, 8s, 16s
                onRetryAsync: (exception, delay, attempt, _) =>
                {
                    _logger.LogWarning(
                        "Повтор запроса к Telegram (попытка {Attempt}) через {Delay}: {Reason}",
                        attempt, delay, exception.Message);
                    return Task.CompletedTask;
                });
    }

    public string? ResolveChannel(Category category) => _o.ResolveChannel(category);

    public async Task<long> SendMessageAsync(string chatId, string text, CancellationToken ct)
    {
        var result = await CallAsync("sendMessage",
            new { chat_id = chatId, text, disable_web_page_preview = true }, chatId, ct);
        return ReadMessageId(result);
    }

    public async Task<long> SendPhotoAsync(string chatId, string photoUrl, string caption, CancellationToken ct)
    {
        var result = await CallAsync("sendPhoto",
            new { chat_id = chatId, photo = photoUrl, caption }, chatId, ct);
        return ReadMessageId(result);
    }

    public async Task<bool> EditPostAsync(string chatId, long messageId, string text, CancellationToken ct)
    {
        try
        {
            await CallAsync("editMessageCaption",
                new { chat_id = chatId, message_id = messageId, caption = text }, chatId, ct);
            return true;
        }
        catch (TelegramApiException ex) when (IsNotModified(ex)) { return true; }
        catch (TelegramApiException ex) when (IsGone(ex)) { return false; }
        catch (TelegramApiException ex) when (IsNoCaption(ex))
        {
            // У поста нет подписи (был отправлен как текст) — правим текст сообщения.
            try
            {
                await CallAsync("editMessageText",
                    new { chat_id = chatId, message_id = messageId, text, disable_web_page_preview = true }, chatId, ct);
                return true;
            }
            catch (TelegramApiException inner) when (IsNotModified(inner)) { return true; }
            catch (TelegramApiException inner) when (IsGone(inner)) { return false; }
        }
    }

    /// <summary>Резервирует слот лимитера и выполняет вызов метода Bot API с ретраями.</summary>
    private async Task<JsonElement> CallAsync(string method, object payload, string chatId, CancellationToken ct)
    {
        // Слот берём один раз на логическое сообщение (до ретраев): ретрай 429 — не новый пост.
        await _limiter.AcquireAsync(chatId, ct);
        return await _retry.ExecuteAsync(token => SendOnceAsync(method, payload, token), ct);
    }

    private async Task<JsonElement> SendOnceAsync(string method, object payload, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync($"bot{_o.BotToken}/{method}", payload, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new TelegramTransientException($"Сеть Telegram: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TelegramTransientException("Таймаут запроса к Telegram.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            TelegramEnvelope? env = null;
            try { env = JsonSerializer.Deserialize<TelegramEnvelope>(body, Json); }
            catch (JsonException) { /* тело не JSON — разберём по HTTP-статусу ниже */ }

            if (env is { Ok: true })
                return env.Result;

            var code = env?.ErrorCode ?? (int)response.StatusCode;
            var description = env?.Description ?? $"HTTP {(int)response.StatusCode}";

            if (code == 429)
            {
                var seconds = env?.Parameters?.RetryAfter
                              ?? (int?)response.Headers.RetryAfter?.Delta?.TotalSeconds
                              ?? 1;
                throw new TelegramRateLimitException(TimeSpan.FromSeconds(Math.Max(1, seconds)), description);
            }

            if (code >= 500)
                throw new TelegramTransientException($"Telegram {code}: {description}");

            throw new TelegramApiException(code, description);
        }
    }

    private static long ReadMessageId(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object && result.TryGetProperty("message_id", out var id)
            ? id.GetInt64()
            : throw new TelegramTransientException("В ответе Telegram нет message_id.");

    private static bool IsGone(TelegramApiException ex)
    {
        var d = ex.Description.ToLowerInvariant();
        return d.Contains("not found") || d.Contains("can't be edited")
            || d.Contains("message_id_invalid") || d.Contains("message to edit not found");
    }

    private static bool IsNoCaption(TelegramApiException ex) =>
        ex.Description.Contains("no caption", StringComparison.OrdinalIgnoreCase);

    private static bool IsNotModified(TelegramApiException ex) =>
        ex.Description.Contains("not modified", StringComparison.OrdinalIgnoreCase);

    // ---- Конверт ответа Bot API ----

    private sealed record TelegramEnvelope
    {
        [JsonPropertyName("ok")] public bool Ok { get; init; }
        [JsonPropertyName("error_code")] public int? ErrorCode { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("result")] public JsonElement Result { get; init; }
        [JsonPropertyName("parameters")] public TelegramResponseParameters? Parameters { get; init; }
    }

    private sealed record TelegramResponseParameters
    {
        [JsonPropertyName("retry_after")] public int? RetryAfter { get; init; }
    }
}
