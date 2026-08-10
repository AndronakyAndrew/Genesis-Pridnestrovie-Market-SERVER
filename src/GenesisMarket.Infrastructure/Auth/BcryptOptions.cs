namespace GenesisMarket.Infrastructure.Auth;

/// <summary>Настройки BCrypt. Секция конфигурации <c>Bcrypt</c>.</summary>
public sealed class BcryptOptions
{
    public const string Section = "Bcrypt";

    /// <summary>Cost-фактор BCrypt. Значение из конфигурации.</summary>
    public int WorkFactor { get; set; } = 12;
}
