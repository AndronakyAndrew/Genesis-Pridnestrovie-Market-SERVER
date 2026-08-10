using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Auth;

/// <summary>
/// Проверка токена сверх стандартной валидации подписи: сверяет claim sstamp
/// с актуальным SecurityStamp пользователя и блокирует забаненных. Без этого
/// logout-all, смена пароля и бан не действовали бы до истечения access-токена.
/// Снимок пользователя кэшируется на короткий TTL (по умолчанию 30 сек).
/// </summary>
public sealed class SecurityStampValidator(
    AppDbContext db,
    IMemoryCache cache,
    IOptions<JwtOptions> options)
{
    private readonly int _ttl = options.Value.SecurityStampCacheSeconds;

    private sealed record Snapshot(Guid SecurityStamp, bool IsBanned, DateTimeOffset? BannedUntil);

    public async Task ValidateAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        var subRaw = principal?.FindFirst("sub")?.Value;
        var sstamp = principal?.FindFirst("sstamp")?.Value;

        if (!Guid.TryParse(subRaw, out var userId) || string.IsNullOrEmpty(sstamp))
        {
            context.Fail("Некорректный токен.");
            return;
        }

        var snapshot = await GetSnapshotAsync(userId, context.HttpContext.RequestAborted);
        if (snapshot is null)
        {
            context.Fail("Пользователь не найден.");
            return;
        }

        if (snapshot.SecurityStamp.ToString() != sstamp)
        {
            context.Fail("Токен отозван.");
            return;
        }

        var banned = snapshot.IsBanned &&
                     (snapshot.BannedUntil is null || snapshot.BannedUntil > DateTimeOffset.UtcNow);
        if (banned)
            context.Fail("Пользователь заблокирован.");
    }

    /// <summary>Сбросить кэш пользователя — вызывается при смене пароля / logout-all.</summary>
    public void Invalidate(Guid userId) => cache.Remove(CacheKey(userId));

    private async Task<Snapshot?> GetSnapshotAsync(Guid userId, CancellationToken ct)
    {
        if (_ttl <= 0)
            return await LoadAsync(userId, ct);

        return await cache.GetOrCreateAsync(CacheKey(userId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_ttl);
            return await LoadAsync(userId, ct);
        });
    }

    private Task<Snapshot?> LoadAsync(Guid userId, CancellationToken ct) =>
        db.Users
            .Where(u => u.Id == userId)
            .Select(u => new Snapshot(u.SecurityStamp, u.IsBanned, u.BannedUntil))
            .Cast<Snapshot?>()
            .FirstOrDefaultAsync(ct);

    private static string CacheKey(Guid userId) => $"sstamp:{userId}";
}
