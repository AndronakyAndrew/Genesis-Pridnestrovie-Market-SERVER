using System.Collections.Concurrent;
using GenesisMarket.Infrastructure.Storage;

namespace GenesisMarket.Tests;

/// <summary>
/// In-memory замена MinIO для интеграционных тестов: хранит байты объектов в
/// словаре, отдаёт «presigned» ссылку заглушкой. Реальный процессор изображений
/// при этом работает по-настоящему — можно проверить результат обработки.
/// </summary>
public sealed class FakeObjectStorage : IObjectStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new();

    public Task<string> PutAsync(string objectName, Stream content, long size, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        content.CopyTo(ms);
        _objects[objectName] = ms.ToArray();
        return Task.FromResult(objectName);
    }

    public Task<Stream> GetAsync(string objectName, CancellationToken ct = default)
    {
        if (!_objects.TryGetValue(objectName, out var bytes))
            throw new FileNotFoundException(objectName);
        return Task.FromResult<Stream>(new MemoryStream(bytes));
    }

    public Task RemoveAsync(string objectName, CancellationToken ct = default)
    {
        _objects.TryRemove(objectName, out _);
        return Task.CompletedTask;
    }

    public Task<string> GetPresignedUrlAsync(string objectName, TimeSpan expiry, CancellationToken ct = default)
        => Task.FromResult($"https://fake-storage.local/{objectName}?ttl={(int)expiry.TotalSeconds}");

    public Task EnsureBucketAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Есть ли объект в хранилище (для проверок в тестах).</summary>
    public bool Exists(string objectName) => _objects.ContainsKey(objectName);

    /// <summary>Возвращает сохранённые байты объекта.</summary>
    public bool TryGet(string objectName, out byte[] bytes)
    {
        if (_objects.TryGetValue(objectName, out var b))
        {
            bytes = b;
            return true;
        }
        bytes = [];
        return false;
    }
}
