namespace GenesisMarket.Api.Auth;

/// <summary>Настройки подтверждения телефона по SMS. Секция <c>PhoneVerification</c>.</summary>
public sealed class PhoneVerificationOptions
{
    public const string Section = "PhoneVerification";

    public int CodeLength { get; set; } = 6;
    public int CodeTtlMinutes { get; set; } = 5;
    public int MaxAttempts { get; set; } = 5;
    public int ResendCooldownSeconds { get; set; } = 60;
}
