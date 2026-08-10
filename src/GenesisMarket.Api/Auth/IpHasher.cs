using System.Security.Cryptography;
using System.Text;

namespace GenesisMarket.Api.Auth;

public interface IIpHasher
{
    /// <summary>HMAC-SHA256 от IP (hex). null, если ключ не задан или IP пуст.</summary>
    string? Hash(string? ip);
}

/// <summary>
/// Хеширует IP через HMAC-SHA256 с ключом из конфигурации
/// (<c>Security:IpHashKey</c>, только из env). Сырой IP в базе не хранится.
/// Если ключ не задан — возвращает null (IP не сохраняется).
/// </summary>
public sealed class HmacIpHasher : IIpHasher
{
    private readonly byte[]? _key;

    public HmacIpHasher(IConfiguration configuration)
    {
        var key = configuration["Security:IpHashKey"];
        _key = string.IsNullOrEmpty(key) ? null : Encoding.UTF8.GetBytes(key);
    }

    public string? Hash(string? ip)
    {
        if (_key is null || string.IsNullOrEmpty(ip))
            return null;

        using var hmac = new HMACSHA256(_key);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(ip)));
    }
}
