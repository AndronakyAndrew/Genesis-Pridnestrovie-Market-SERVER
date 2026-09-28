using GenesisMarket.Domain.Entities;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GenesisMarket.Api.Auth;

/// <summary>
/// Сохранение нового аккаунта — общее для регистрации и приглашения продавца.
/// Проверка занятости в <see cref="IPublicCodeGenerator"/> и сама вставка не атомарны:
/// две одновременные регистрации могут выбрать один и тот же свободный код и обе пройти
/// проверку. Победит одна, второй уникальный индекс вернёт 23505 — тогда берём
/// следующий код, а не отдаём 500.
/// </summary>
public static class NewAccounts
{
    private const int MaxRetries = 3;

    public static async Task SaveAsync(
        AppDbContext db, IPublicCodeGenerator publicCodes, User user, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (attempt < MaxRetries && IsPublicCodeConflict(ex))
            {
                user.PublicCode = await publicCodes.NextAsync(ct);
            }
        }
    }

    private static bool IsPublicCodeConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: "23505" } pg &&
        pg.ConstraintName == "IX_users_PublicCode";
}
