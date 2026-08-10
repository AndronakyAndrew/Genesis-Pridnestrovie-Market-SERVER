namespace GenesisMarket.Infrastructure.Auth;

/// <summary>
/// Хеширование и проверка паролей. Реализация — BCrypt (Enhanced).
/// </summary>
public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string hash);

    /// <summary>Нужно ли пересчитать хеш (изменился workFactor).</summary>
    bool NeedsRehash(string hash);

    /// <summary>
    /// Заранее посчитанный фиктивный хеш. Используется при отсутствии пользователя,
    /// чтобы время ответа не отличалось от случая с реальным пользователем
    /// (защита от перечисления по времени).
    /// </summary>
    string DummyHash { get; }
}
