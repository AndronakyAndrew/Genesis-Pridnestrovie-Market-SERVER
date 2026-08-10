using System.Text;

namespace GenesisMarket.Infrastructure.Auth;

public enum PasswordCheck
{
    Ok,
    TooShort,
    TooLong,
    TooCommon
}

public interface IPasswordPolicy
{
    PasswordCheck Validate(string password);
}

/// <summary>
/// Политика паролей: длина 8..72 БАЙТА в UTF-8 (не символа — иначе кириллица
/// обрежется молча на входе BCrypt) + запрет частых паролей из блок-листа.
/// </summary>
public sealed class PasswordPolicy(CommonPasswords common) : IPasswordPolicy
{
    public const int MinBytes = 8;
    public const int MaxBytes = 72;

    public PasswordCheck Validate(string password)
    {
        var bytes = Encoding.UTF8.GetByteCount(password);
        if (bytes < MinBytes) return PasswordCheck.TooShort;
        if (bytes > MaxBytes) return PasswordCheck.TooLong;
        if (common.Contains(password)) return PasswordCheck.TooCommon;
        return PasswordCheck.Ok;
    }
}
