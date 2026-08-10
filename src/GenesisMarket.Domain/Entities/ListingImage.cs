namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Изображение объявления. Отдельная таблица (не массив строк): нужны порядок
/// (<see cref="SortOrder"/>), метаданные и каскадное удаление вместе с объявлением.
/// Файлы лежат в объектном хранилище, здесь — только ключи.
/// </summary>
public class ListingImage
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ListingId { get; set; }
    public Listing? Listing { get; set; }

    /// <summary>Ключ оригинала в объектном хранилище.</summary>
    public required string ObjectKey { get; set; }

    /// <summary>Ключ превью (миниатюры).</summary>
    public required string ThumbKey { get; set; }

    public int SortOrder { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
