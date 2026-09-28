using System.Security.Cryptography;
using System.Text;

namespace GenesisMarket.Api.Auth.TwoFactor;

/// <summary>
/// TOTP по RFC 6238 (HMAC-SHA1, шаг 30 с, 6 цифр) — ровно то, что понимают Google
/// Authenticator, Aegis, 1Password и прочие приложения. Реализация своя, а не пакет:
/// алгоритм — десяток строк поверх HMAC из BCL, и проверяется тестовыми векторами RFC.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;

    /// <summary>Размер секрета: 160 бит, рекомендация RFC 4226 для HMAC-SHA1.</summary>
    public const int SecretBytes = 20;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public static long TimeStep(DateTimeOffset at) => at.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>Код для шага времени (RFC 4226, динамическое усечение).</summary>
    public static string Compute(byte[] secret, long timeStep)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counter[i] = (byte)(timeStep & 0xFF);
            timeStep >>= 8;
        }

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);

        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
                     | (hash[offset + 1] << 16)
                     | (hash[offset + 2] << 8)
                     | hash[offset + 3];

        return (binary % 1_000_000).ToString("D6");
    }

    /// <summary>
    /// Проверка кода с допуском ±1 шаг (рассинхрон часов телефона). Возвращает принятый
    /// шаг или null. Шаги не новее <paramref name="lastUsedStep"/> не принимаются: один
    /// и тот же код дважды не проходит. Сравнение — в постоянном времени.
    /// </summary>
    public static long? Verify(byte[] secret, string? code, DateTimeOffset now, long lastUsedStep)
    {
        var normalized = Normalize(code);
        if (normalized is null)
            return null;

        var expected = Encoding.ASCII.GetBytes(normalized);
        var current = TimeStep(now);
        long? matched = null;

        // Перебираем все три окна без раннего выхода — время ответа не зависит от того,
        // в каком окне совпало.
        for (var step = current - 1; step <= current + 1; step++)
        {
            var candidate = Encoding.ASCII.GetBytes(Compute(secret, step));
            if (CryptographicOperations.FixedTimeEquals(candidate, expected) && step > lastUsedStep)
                matched ??= step;
        }

        return matched;
    }

    /// <summary>
    /// Ссылка <c>otpauth://</c> для QR-кода. QR строится на клиенте: секрет не уходит
    /// ни в какой сторонний генератор картинок.
    /// </summary>
    public static string ProvisioningUri(string issuer, string accountLabel, byte[] secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(accountLabel)}" +
        $"?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}" +
        $"&algorithm=SHA1&digits={Digits}&period={StepSeconds}";

    private static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;
        var digits = new string(code.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        return digits.Length == Digits && digits.All(char.IsAsciiDigit) ? digits : null;
    }
}

/// <summary>Base32 (RFC 4648) без выравнивания — формат секрета в otpauth-ссылках.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }
        }
        if (bits > 0)
            sb.Append(Alphabet[(buffer << (5 - bits)) & 0x1F]);
        return sb.ToString();
    }
}
