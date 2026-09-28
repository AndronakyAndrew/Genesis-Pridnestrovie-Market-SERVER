using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using GenesisMarket.Api.Auth;
using GenesisMarket.Infrastructure.Invitations;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Invitations;

/// <summary>Токен ссылки: 32 случайных байта наружу (base64url), в БД — SHA-256.</summary>
public static class InvitationTokens
{
    public static (string Raw, byte[] Hash) Generate()
    {
        var raw = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (raw, Hash(raw));
    }

    public static byte[] Hash(string raw) => SHA256.HashData(Encoding.UTF8.GetBytes(raw));

    /// <summary>Форма токена проверяется до обращения к БД: 32 байта в base64url — 43 символа.</summary>
    public static bool IsWellFormed(string? raw) =>
        raw is { Length: 43 } && raw.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>
/// Подписанные ссылки на фото черновиков. Фото приглашения не лежат под публичным
/// префиксом <c>listings/</c>: их отдаёт отдельная ручка только по действующей подписи
/// (фото, вариант, срок). Подпись — HMAC-SHA256 ключом, выведенным из <c>Jwt:Key</c>
/// (HKDF с отдельной меткой): тег &lt;img&gt; не умеет слать Authorization, а ключ в
/// query-строке ограничен и по объекту, и по времени.
/// </summary>
public sealed class InvitationPhotoUrls
{
    private readonly byte[] _key;
    private readonly TimeSpan _ttl;

    public InvitationPhotoUrls(IOptions<JwtOptions> jwt, IOptions<InvitationOptions> options)
    {
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(jwt.Value.Key), 32,
            info: Encoding.ASCII.GetBytes("genesis-invitation-photo-url-v1"));
        _ttl = TimeSpan.FromMinutes(Math.Max(1, options.Value.PhotoUrlMinutes));
    }

    public string Build(HttpRequest request, Guid photoId, bool thumb)
    {
        var exp = DateTimeOffset.UtcNow.Add(_ttl).ToUnixTimeSeconds();
        var sig = Sign(photoId, thumb, exp);
        return $"{request.Scheme}://{request.Host}/api/invitation-photos/{photoId}?v={(thumb ? "t" : "o")}&exp={exp}&sig={sig}";
    }

    public bool Verify(Guid photoId, bool thumb, long exp, string? sig)
    {
        if (string.IsNullOrEmpty(sig) || DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp)
            return false;
        var expected = Encoding.ASCII.GetBytes(Sign(photoId, thumb, exp));
        var actual = Encoding.ASCII.GetBytes(sig);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private string Sign(Guid photoId, bool thumb, long exp) =>
        Base64Url.EncodeToString(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes($"{photoId:N}|{(thumb ? 't' : 'o')}|{exp}")));
}
