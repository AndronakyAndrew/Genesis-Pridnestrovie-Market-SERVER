namespace GenesisMarket.Tests;

/// <summary>
/// Временные переменные окружения для отдельного хоста. Program читает часть настроек ещё до
/// Build (rate-limit, строка подключения, токен outbox), поэтому переопределять их можно только
/// через окружение, а оно живёт на весь тестовый процесс — при Dispose возвращаем прежние значения.
/// </summary>
internal sealed class EnvScope : IDisposable
{
    private readonly (string Name, string? Previous)[] _saved;

    public EnvScope(params (string Name, string Value)[] vars)
    {
        _saved = vars.Select(v => (v.Name, Environment.GetEnvironmentVariable(v.Name))).ToArray();
        foreach (var (name, value) in vars)
            Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (var (name, previous) in _saved)
            Environment.SetEnvironmentVariable(name, previous);
    }
}
