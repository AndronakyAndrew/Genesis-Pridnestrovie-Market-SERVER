using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

namespace GenesisMarket.Infrastructure.Storage;

/// <summary>
/// Readiness-проверка MinIO: убеждаемся, что рабочий бакет доступен.
/// </summary>
public class MinioHealthCheck(IMinioClient client, IOptions<MinioOptions> options) : IHealthCheck
{
    private readonly MinioOptions _options = options.Value;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var exists = await client.BucketExistsAsync(
                new BucketExistsArgs().WithBucket(_options.Bucket),
                cancellationToken);

            return exists
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"Бакет '{_options.Bucket}' не найден.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("MinIO недоступен.", ex);
        }
    }
}
