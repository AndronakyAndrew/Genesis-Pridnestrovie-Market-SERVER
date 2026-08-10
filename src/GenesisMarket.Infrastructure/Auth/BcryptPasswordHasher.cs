using Microsoft.Extensions.Options;
using BC = BCrypt.Net.BCrypt;

namespace GenesisMarket.Infrastructure.Auth;

/// <summary>
/// BCrypt-хешер. Используется EnhancedHashPassword/EnhancedVerify
/// (SHA-384 pre-hash — вход не обрезается на 72 байтах молча).
/// Сравнение хешей — только через EnhancedVerify, никаких == / SequenceEqual.
/// </summary>
public sealed class BcryptPasswordHasher : IPasswordHasher
{
    private readonly int _workFactor;

    public string DummyHash { get; }

    public BcryptPasswordHasher(IOptions<BcryptOptions> options)
    {
        _workFactor = options.Value.WorkFactor;
        // Считается один раз при старте — сверяемся с ним, когда пользователь не найден.
        DummyHash = BC.EnhancedHashPassword("::genesis-timing-dummy::", _workFactor);
    }

    public string Hash(string password) => BC.EnhancedHashPassword(password, _workFactor);

    public bool Verify(string password, string hash) => BC.EnhancedVerify(password, hash);

    public bool NeedsRehash(string hash) => BC.PasswordNeedsRehash(hash, _workFactor);
}
