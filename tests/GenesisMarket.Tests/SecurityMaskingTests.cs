using GenesisMarket.Api.Security;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Маскирование секретов при деструктуризации Serilog (<c>{@obj}</c>): даже если DTO
/// логина/регистрации залогируют целиком, password/token/phone/email не утекут.
/// </summary>
public class SecurityMaskingTests
{
    // Тип в пространстве имён "GenesisMarket*" — политика применяется только к своим типам.
    private sealed record SampleCreds(string Email, string Password, string DisplayName, string RefreshToken);

    [Fact]
    public void Sensitive_properties_are_masked_others_kept()
    {
        var policy = new MaskingDestructuringPolicy();
        var creds = new SampleCreds("user@test.io", "CorrectHorse7", "Иван", "raw-refresh-token");

        Assert.True(policy.TryDestructure(creds, new ScalarFactory(), out var result));
        var structure = Assert.IsType<StructureValue>(result);
        var byName = structure.Properties.ToDictionary(p => p.Name, p => ((ScalarValue)p.Value).Value);

        Assert.Equal("***", byName["Password"]);
        Assert.Equal("***", byName["Email"]);
        Assert.Equal("***", byName["RefreshToken"]);
        // Несекретные поля остаются как есть.
        Assert.Equal("Иван", byName["DisplayName"]);
    }

    [Fact]
    public void Foreign_types_are_not_intercepted()
    {
        // Тип фреймворка (не GenesisMarket) политика не трогает — возвращает false.
        var policy = new MaskingDestructuringPolicy();
        Assert.False(policy.TryDestructure(new Uri("https://example.com"), new ScalarFactory(), out _));
    }

    private sealed class ScalarFactory : ILogEventPropertyValueFactory
    {
        public LogEventPropertyValue CreatePropertyValue(object? value, bool destructureObjects) =>
            new ScalarValue(value);
    }
}
