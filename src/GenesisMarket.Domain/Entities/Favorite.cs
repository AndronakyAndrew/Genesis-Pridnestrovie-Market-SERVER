namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Избранное. Составной первичный ключ (UserId, ListingId) — идемпотентность
/// на уровне БД: повторное добавление не создаёт дубликат.
/// </summary>
public class Favorite
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid ListingId { get; set; }
    public Listing? Listing { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
