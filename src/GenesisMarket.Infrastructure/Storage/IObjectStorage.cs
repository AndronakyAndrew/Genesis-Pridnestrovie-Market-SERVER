namespace GenesisMarket.Infrastructure.Storage;

/// <summary>
/// Абстракция хранилища объектов (фото объявлений). Реализация — MinIO/S3.
/// </summary>
public interface IObjectStorage
{
    Task<string> PutAsync(
        string objectName,
        Stream content,
        long size,
        string contentType,
        CancellationToken ct = default);

    Task<Stream> GetAsync(string objectName, CancellationToken ct = default);

    Task RemoveAsync(string objectName, CancellationToken ct = default);
}
