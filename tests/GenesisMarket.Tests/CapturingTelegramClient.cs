using System.Collections.Concurrent;
using GenesisMarket.Api.Outbox.Telegram;
using GenesisMarket.Domain.Enums;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Tests;

/// <summary>
/// Тестовый двойник Telegram-клиента: не ходит в сеть, а записывает вызовы (посты и правки),
/// выдавая синтетические message_id. Маршрутизацию канала берёт из реальных
/// <see cref="TelegramOptions"/>, чтобы проверять «категория → канал» из конфигурации.
/// </summary>
public sealed class CapturingTelegramClient(IOptions<TelegramOptions> options) : ITelegramClient
{
    public sealed record SentPost(string Method, string ChatId, string? PhotoUrl, string Text, long MessageId);
    public sealed record EditedPost(string ChatId, long MessageId, string Text);

    private readonly ConcurrentQueue<SentPost> _sends = new();
    private readonly ConcurrentQueue<EditedPost> _edits = new();
    private long _counter;

    public IReadOnlyList<SentPost> Sends => _sends.ToArray();
    public IReadOnlyList<EditedPost> Edits => _edits.ToArray();

    public string? ResolveChannel(Category category) => options.Value.ResolveChannel(category);

    public Task<long> SendMessageAsync(string chatId, string text, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _counter);
        _sends.Enqueue(new SentPost("sendMessage", chatId, null, text, id));
        return Task.FromResult(id);
    }

    public Task<long> SendPhotoAsync(string chatId, string photoUrl, string caption, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _counter);
        _sends.Enqueue(new SentPost("sendPhoto", chatId, photoUrl, caption, id));
        return Task.FromResult(id);
    }

    public Task<bool> EditPostAsync(string chatId, long messageId, string text, CancellationToken ct)
    {
        _edits.Enqueue(new EditedPost(chatId, messageId, text));
        return Task.FromResult(true);
    }
}
