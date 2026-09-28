using System.Security.Cryptography;
using System.Text;

namespace GenesisMarket.Api.Auth.TwoFactor;

/// <summary>
/// Шифрование секретов TOTP и хеширование резервных кодов. Ключ — <c>Security:TotpKey</c>
/// (base64, 32 байта), только из окружения. Секрет TOTP, в отличие от пароля, нельзя
/// хранить хешем: серверу нужен сам секрет, чтобы считать коды. Поэтому AES-256-GCM:
/// дамп базы без ключа не даёт сгенерировать ни одного кода.
///
/// Из одного мастер-ключа выводятся два подключа (HKDF с разными метками) — ключ
/// шифрования и ключ HMAC резервных кодов не совпадают.
/// </summary>
public interface ITwoFactorSecretProtector
{
    byte[] Protect(byte[] secret);
    byte[] Unprotect(byte[] cipher);
    byte[] HashRecoveryCode(string normalizedCode);
}

public sealed class AesGcmTwoFactorSecretProtector : ITwoFactorSecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _encryptionKey;
    private readonly byte[] _recoveryKey;

    public AesGcmTwoFactorSecretProtector(byte[] masterKey)
    {
        if (masterKey.Length != 32)
            throw new InvalidOperationException("Security:TotpKey должен быть 32 байта в base64.");

        _encryptionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32,
            info: Encoding.ASCII.GetBytes("genesis-totp-secret-v1"));
        _recoveryKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32,
            info: Encoding.ASCII.GetBytes("genesis-recovery-code-v1"));
    }

    /// <summary>nonce ‖ tag ‖ ciphertext.</summary>
    public byte[] Protect(byte[] secret)
    {
        var output = new byte[NonceSize + TagSize + secret.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_encryptionKey, TagSize);
        aes.Encrypt(nonce, secret, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize));
        return output;
    }

    public byte[] Unprotect(byte[] cipher)
    {
        if (cipher.Length <= NonceSize + TagSize)
            throw new CryptographicException("Повреждённый секрет 2FA.");

        var plain = new byte[cipher.Length - NonceSize - TagSize];
        using var aes = new AesGcm(_encryptionKey, TagSize);
        aes.Decrypt(cipher.AsSpan(0, NonceSize), cipher.AsSpan(NonceSize + TagSize),
            cipher.AsSpan(NonceSize, TagSize), plain);
        return plain;
    }

    public byte[] HashRecoveryCode(string normalizedCode) =>
        HMACSHA256.HashData(_recoveryKey, Encoding.ASCII.GetBytes(normalizedCode));
}
