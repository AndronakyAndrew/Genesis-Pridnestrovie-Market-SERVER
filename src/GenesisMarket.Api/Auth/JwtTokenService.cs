using System.Security.Cryptography;
using System.Text;
using GenesisMarket.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace GenesisMarket.Api.Auth;

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

public interface ITokenService
{
    AccessToken CreateAccessToken(User user);
}

/// <summary>
/// Выпуск access-токенов (HS256). Claims: sub (UserId), role, sstamp
/// (SecurityStamp), jti. Email в токен НЕ кладём.
/// </summary>
public sealed class JwtTokenService(IOptions<JwtOptions> options) : ITokenService
{
    private readonly JwtOptions _o = options.Value;

    public AccessToken CreateAccessToken(User user)
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
                ["role"] = user.Role.ToString(),
                ["sstamp"] = user.SecurityStamp.ToString(),
                ["jti"] = RandomNumberGenerator.GetHexString(32)
            },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        };

        var token = new JsonWebTokenHandler().CreateToken(descriptor);
        return new AccessToken(token, expires);
    }
}
