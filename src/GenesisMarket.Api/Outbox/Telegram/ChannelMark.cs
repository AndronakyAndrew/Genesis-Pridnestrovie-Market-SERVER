namespace GenesisMarket.Api.Outbox.Telegram;

/// <summary>
/// Правка поста объявления в канале — значение <c>mark</c> в payload
/// <see cref="Domain.Entities.OutboxMessage.ListingChannelUpdate"/>. Сами правки выполняет
/// <see cref="Api.Telegram.Channel.IChannelPublisher"/>.
/// </summary>
public static class ChannelMark
{
    /// <summary>Заголовок + «✅ ПРОДАНО», без кнопки.</summary>
    public const string Sold = "sold";

    /// <summary>Заголовок + «⛔ Снято с публикации», без кнопки (архив, отказ, повторная проверка).</summary>
    public const string Archived = "archived";

    /// <summary>Полная подпись и кнопка — объявление снова активно.</summary>
    public const string Active = "active";
}
