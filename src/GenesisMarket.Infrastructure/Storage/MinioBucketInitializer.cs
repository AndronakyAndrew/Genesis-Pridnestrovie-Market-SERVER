using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GenesisMarket.Infrastructure.Storage;

/// <summary>
/// При старте гарантирует существование приватного бакета для фото. Ошибку не
/// эскалирует: недоступность MinIO на старте не должна ронять весь API — от
/// хранилища зависит только загрузка изображений.
/// </summary>
public sealed class MinioBucketInitializer(
    IServiceProvider services,
    ILogger<MinioBucketInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = services.CreateScope();
            var storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
            await storage.EnsureBucketAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось инициализировать бакет MinIO при старте");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
