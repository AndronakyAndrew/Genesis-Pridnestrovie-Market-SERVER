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

    /// <summary>
    /// Presigned URL на чтение объекта с ограниченным сроком жизни. Наружу отдаётся
    /// именно он — прямые постоянные ссылки на приватный бакет не публикуются.
    /// </summary>
    Task<string> GetPresignedUrlAsync(string objectName, TimeSpan expiry, CancellationToken ct = default);

    /// <summary>Создаёт рабочий бакет, если его ещё нет. Публичную политику не выставляет.</summary>
    Task EnsureBucketAsync(CancellationToken ct = default);
}
