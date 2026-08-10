namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Сообщение в диалоге. Писать может только участник диалога (проверка на сервере).
/// </summary>
public class Message
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ConversationId { get; set; }
    public Conversation? Conversation { get; set; }

    public Guid SenderId { get; set; }
    public User? Sender { get; set; }

    /// <summary>Текст сообщения, до 2000 символов (CHECK на уровне БД).</summary>
    public required string Text { get; set; }

    public bool IsRead { get; set; }
    public bool IsDeleted { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
