namespace GenesisMarket.Api.Contracts;

/// <summary>
/// Изображение объявления. <see cref="Url"/> и <see cref="ThumbUrl"/> — публичные URL
/// API (<c>/api/images/listings/...</c>). MinIO в приватной сети, поэтому presigned-ссылки
/// на бакет браузеру не отдаём.
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
