using System.Collections.Concurrent;
using System.Text.Json;
using GenesisMarket.Api.Telegram;

namespace GenesisMarket.Tests;

/// <summary>
/// Тестовый двойник клиента бота: в сеть не ходит, записывает посты с фото и правки подписей,
/// выдаёт синтетические message_id. Отправку можно заставить упасть — для проверки ретраев.
/// </summary>
public sealed class CapturingBotClient : ITelegramClient
{
    public sealed record SentPhoto(string ChatId, string PhotoUrl, string Caption, InlineButton? Button, int MessageId);
    public sealed record EditedCaption(string ChatId, int MessageId, string Caption, InlineButton? Button);

    private readonly ConcurrentQueue<SentPhoto> _photos = new();
    private readonly ConcurrentQueue<EditedCaption> _edits = new();
    private readonly ConcurrentQueue<Exception> _sendFailures = new();
    private int _counter = 1000;

    public IReadOnlyList<SentPhoto> Photos => _photos.ToArray();
    public IReadOnlyList<EditedCaption> Edits => _edits.ToArray();

    /// <summary>Следующая отправка фото упадёт этим исключением (по одному на вызов).</summary>
    public void FailNextSend(Exception ex) => _sendFailures.Enqueue(ex);

    /// <summary>Посты с кнопкой на это объявление.</summary>
    public IReadOnlyList<SentPhoto> PhotosFor(Guid listingId) =>
        Photos.Where(p => p.Button?.Url.Contains(listingId.ToString()) == true).ToList();

    public Task<TelegramBotInfo> GetMeAsync(CancellationToken ct = default) =>
        Task.FromResult(new TelegramBotInfo(1, "genesis_test_bot", "Genesis"));

    public Task<int> SendPhotoAsync(string chatId, string photoUrl, string caption,
        InlineButton? button = null, CancellationToken ct = default)
    {
        if (_sendFailures.TryDequeue(out var failure))
            return Task.FromException<int>(failure);

        var id = Interlocked.Increment(ref _counter);
        _photos.Enqueue(new SentPhoto(chatId, photoUrl, caption, button, id));
        return Task.FromResult(id);
    }

    public Task EditCaptionAsync(string chatId, int messageId, string caption,
        InlineButton? button = null, CancellationToken ct = default)
    {
        _edits.Enqueue(new EditedCaption(chatId, messageId, caption, button));
        return Task.CompletedTask;
    }

    public Task<int> SendMessageAsync(string chatId, string text, InlineButton? button = null,
        bool disablePreview = true, CancellationToken ct = default) =>
        Task.FromException<int>(new NotSupportedException("В канал публикуется только sendPhoto."));

    public Task DeleteMessageAsync(string chatId, int messageId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<JsonElement> CallAsync(string method, object? payload, CancellationToken ct = default) =>
        Task.FromException<JsonElement>(new NotSupportedException($"Метод {method} в тестах не эмулируется."));
}
