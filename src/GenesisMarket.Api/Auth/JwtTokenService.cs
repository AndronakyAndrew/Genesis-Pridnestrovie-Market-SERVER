using System.Security.Cryptography;
using System.Text;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace GenesisMarket.Api.Auth;

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

public interface ITokenService
{
    /// <summary>
    /// sessionId уходит в claim sid: по нему запрос узнаёт свою сессию. role — роль в
    /// проекте из IRoleStore (в users её нет). mfa — сессия прошла второй фактор:
    /// без него служебные разрешения не действуют.
    /// </summary>
    AccessToken CreateAccessToken(User user, UserRole role, Guid sessionId, bool mfa);
}

/// <summary>
/// Выпуск access-токенов (HS256). Claims: sub (UserId), role, sstamp
/// (SecurityStamp), sid (идентификатор сессии), mfa (только если пройдена 2FA), jti.
/// Email в токен НЕ кладём.
/// </summary>
public sealed class JwtTokenService(IOptions<JwtOptions> options) : ITokenService
{
    private readonly JwtOptions _o = options.Value;

    public AccessToken CreateAccessToken(User user, UserRole role, Guid sessionId, bool mfa)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(_o.AccessTokenMinutes);

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.Key));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _o.Issuer,
            Audience = _o.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = user.Id.ToString(),
                ["role"] = role.ToString(),
                ["sstamp"] = user.SecurityStamp.ToString(),
                ["sid"] = sessionId.ToString(),
                ["jti"] = RandomNumberGenerator.GetHexString(32)
            },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        };
        if (mfa)
            descriptor.Claims[MfaClaim.Type] = MfaClaim.Verified;

        var token = new JsonWebTokenHandler().CreateToken(descriptor);
        return new AccessToken(token, expires);
    }
}
