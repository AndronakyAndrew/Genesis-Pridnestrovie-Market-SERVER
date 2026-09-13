using System.Collections.Concurrent;
using GenesisMarket.Api.Auth;

namespace GenesisMarket.Tests;

/// <summary>
/// Источник публичных кодов с подменяемой последовательностью: тест кладёт в
/// очередь конкретные коды (например, заведомо занятые), остальное отдаёт
/// настоящий криптослучайный источник. Нужен, чтобы коллизия была
/// воспроизводимой, а не ловилась один раз на 90 000 регистраций.
/// </summary>
public sealed class ScriptedPublicCodeSource : IPublicCodeSource
{
    private readonly ConcurrentQueue<string> _scripted = new();
    private readonly RandomPublicCodeSource _random = new();
    private readonly ConcurrentQueue<string> _issued = new();

    /// <summary>Коды, которые будут выданы ближайшими вызовами — по порядку.</summary>
    public void Script(params string[] codes)
    {
        foreach (var code in codes)
            _scripted.Enqueue(code);
    }

    /// <summary>Всё, что источник отдал, в порядке выдачи — для проверок в тесте.</summary>
    public IReadOnlyList<string> Issued => [.. _issued];

    public string Next()
    {
        var code = _scripted.TryDequeue(out var scripted) ? scripted : _random.Next();
        _issued.Enqueue(code);
        return code;
    }
}
