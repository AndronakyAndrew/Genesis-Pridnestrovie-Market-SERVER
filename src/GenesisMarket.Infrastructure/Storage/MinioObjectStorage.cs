using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

namespace GenesisMarket.Infrastructure.Storage;

/// <summary>Хранилище объектов поверх MinIO (S3-совместимое API).</summary>
public class MinioObjectStorage(IMinioClient client, IOptions<MinioOptions> options) : IObjectStorage
{
    private readonly MinioOptions _options = options.Value;

    public async Task<string> PutAsync(
        string objectName,
        Stream content,
        long size,
        string contentType,
        CancellationToken ct = default)
    {
        var args = new PutObjectArgs()
            .WithBucket(_options.Bucket)
            .WithObject(objectName)
            .WithStreamData(content)
            .WithObjectSize(size)
            .WithContentType(contentType);

        await client.PutObjectAsync(args, ct);
        return objectName;
    }

    public async Task<Stream> GetAsync(string objectName, CancellationToken ct = default)
    {
        var ms = new MemoryStream();
        var args = new GetObjectArgs()
            .WithBucket(_options.Bucket)
            .WithObject(objectName)
            .WithCallbackStream(s => s.CopyTo(ms));

        await client.GetObjectAsync(args, ct);
        ms.Position = 0;
        return ms;
    }

    public async Task RemoveAsync(string objectName, CancellationToken ct = default)
    {
        var args = new RemoveObjectArgs()
            .WithBucket(_options.Bucket)
            .WithObject(objectName);

        await client.RemoveObjectAsync(args, ct);
    }

    public async Task<string> GetPresignedUrlAsync(string objectName, TimeSpan expiry, CancellationToken ct = default)
    {
        var args = new PresignedGetObjectArgs()
            .WithBucket(_options.Bucket)
            .WithObject(objectName)
            .WithExpiry((int)expiry.TotalSeconds);

        return await client.PresignedGetObjectAsync(args);
    }

    public async Task EnsureBucketAsync(CancellationToken ct = default)
    {
        var exists = await client.BucketExistsAsync(
            new BucketExistsArgs().WithBucket(_options.Bucket), ct);

        if (!exists)
            await client.MakeBucketAsync(new MakeBucketArgs().WithBucket(_options.Bucket), ct);

        // Публичную политику доступа НЕ устанавливаем: бакет остаётся приватным,
        // отдача — только через presigned URL с ограниченным TTL.
    }
}
