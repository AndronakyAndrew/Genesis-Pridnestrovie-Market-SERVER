namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Диалог покупатель↔продавец по конкретному объявлению.
/// Один диалог на пару (Listing, Buyer).
/// </summary>
public class Conversation
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ListingId { get; set; }
    public Listing? Listing { get; set; }

    public Guid BuyerId { get; set; }
    public User? Buyer { get; set; }

    public Guid SellerId { get; set; }
    public User? Seller { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastMessageAt { get; set; }

    public ICollection<Message> Messages { get; set; } = new List<Message>();
}
