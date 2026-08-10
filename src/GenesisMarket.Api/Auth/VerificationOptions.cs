namespace GenesisMarket.Api.Auth;

/// <summary>
/// Настройки кодов подтверждения (общие для почты и телефона).
/// Секция <c>Verification</c>.
/// </summary>
public sealed class VerificationOptions
{
    public const string Section = "Verification";

    public int CodeLength { get; set; } = 6;
    public int CodeTtlMinutes { get; set; } = 5;
    public int MaxAttempts { get; set; } = 5;
    public int ResendCooldownSeconds { get; set; } = 60;
}
