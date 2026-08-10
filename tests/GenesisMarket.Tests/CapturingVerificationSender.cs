using System.Collections.Concurrent;
using GenesisMarket.Api.Auth;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Tests;

/// <summary>
/// Тестовый двойник отправителя кодов: не шлёт ничего, а запоминает последний
/// код по цели (email/телефон), чтобы тест мог его прочитать и подтвердить.
/// </summary>
public sealed class CapturingVerificationSender : IVerificationSender
{
    private readonly ConcurrentDictionary<string, string> _codes = new();

    public Task SendCodeAsync(
        VerificationChannel channel, string target, string code, int ttlMinutes, CancellationToken ct)
    {
        _codes[target] = code;
        return Task.CompletedTask;
    }

    public string? Last(string target) => _codes.TryGetValue(target, out var code) ? code : null;
}
