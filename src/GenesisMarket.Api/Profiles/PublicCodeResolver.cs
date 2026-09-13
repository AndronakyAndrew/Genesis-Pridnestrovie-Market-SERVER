using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Profiles;

/// <summary>
/// Переводит «ID профиля» («82914») во внутренний Guid. Единственная точка
/// такого перевода: публичные адреса профиля, отзывов и аватара работают по коду,
/// а Guid дальше контроллера не уходит.
/// </summary>
public interface IPublicCodeResolver
{
    /// <summary>Id пользователя по коду. null — кода нет, он не той формы или аккаунт удалён.</summary>
    Task<Guid?> ResolveAsync(string? code, CancellationToken ct);
}

public sealed class PublicCodeResolver(AppDbContext db) : IPublicCodeResolver
{
    public async Task<Guid?> ResolveAsync(string? code, CancellationToken ct)
    {
        // Форму проверяем до запроса: «by-code/abc» не должен уходить в БД.
        if (!IsWellFormed(code))
            return null;

        var id = await db.Users.AsNoTracking()
            .Where(u => u.PublicCode == code && !u.IsDeleted)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct);

        return id;
    }

    private static bool IsWellFormed(string? code) =>
        code is { Length: Auth.PublicCode.Length } && code.All(char.IsAsciiDigit);
}
