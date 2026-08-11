namespace GenesisMarket.Api.Contracts;

/// <summary>
/// Изображение объявления. <see cref="Url"/> и <see cref="ThumbUrl"/> — presigned-ссылки
/// с ограниченным TTL; прямые постоянные ссылки на приватный бакет не отдаются.
/// </summary>
public record ListingImageResponse(
    Guid Id,
    int SortOrder,
    int Width,
    int Height,
    string Url,
    string ThumbUrl);

/// <summary>Новый порядок изображений: полный набор Id объявления в нужной последовательности.</summary>
public record ReorderImagesRequest(IReadOnlyList<Guid> ImageIds);
