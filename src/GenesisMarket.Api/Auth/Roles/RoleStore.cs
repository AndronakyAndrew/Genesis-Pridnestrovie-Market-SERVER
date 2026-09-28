using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Auth.Roles;

/// <summary>
/// Хранилище ролей «пользователь + проект + роль». Единственная точка чтения и записи
/// ролей в приложении: при переносе в Genesis Ops заменяется реализация (HTTP-клиент),
/// а выпуск токенов, политики и правила смены ролей остаются как есть.
///
/// Запись идёт в текущий DbContext без сохранения — вызывающий коммитит её в одной
/// транзакции с журналом и ротацией SecurityStamp (см. <see cref="RoleManagementService"/>).
/// </summary>
public interface IRoleStore
{
    /// <summary>Проект, роли которого обслуживает хранилище.</summary>
    string ProjectKey { get; }

    Task<UserRole> GetRoleAsync(Guid userId, CancellationToken ct);

    /// <summary>Сотрудники проекта (все, у кого роль выше User).</summary>
    Task<IReadOnlyList<StaffAssignment>> ListStaffAsync(CancellationToken ct);

    /// <summary>Текущий владелец проекта, если назначен.</summary>
    Task<Guid?> GetOwnerAsync(CancellationToken ct);

    /// <summary>Поставить роль в очередь на запись. <see cref="UserRole.User"/> — снять служебную роль.</summary>
    Task SetRoleAsync(Guid userId, UserRole role, Guid? grantedBy, CancellationToken ct);
}

public sealed record StaffAssignment(Guid UserId, UserRole Role, Guid? GrantedByUserId, DateTimeOffset GrantedAt);

public sealed class DbRoleStore(AppDbContext db) : IRoleStore
{
    public string ProjectKey => Projects.Market;

    public async Task<UserRole> GetRoleAsync(Guid userId, CancellationToken ct) =>
        await db.ProjectRoles.AsNoTracking()
            .Where(a => a.UserId == userId && a.ProjectKey == ProjectKey)
            .Select(a => (UserRole?)a.Role)
            .FirstOrDefaultAsync(ct) ?? UserRole.User;

    public async Task<IReadOnlyList<StaffAssignment>> ListStaffAsync(CancellationToken ct) =>
        await db.ProjectRoles.AsNoTracking()
            .Where(a => a.ProjectKey == ProjectKey)
            .OrderByDescending(a => a.Role).ThenBy(a => a.GrantedAt)
            .Select(a => new StaffAssignment(a.UserId, a.Role, a.GrantedByUserId, a.GrantedAt))
            .ToListAsync(ct);

    public Task<Guid?> GetOwnerAsync(CancellationToken ct) =>
        db.ProjectRoles.AsNoTracking()
            .Where(a => a.ProjectKey == ProjectKey && a.Role == UserRole.Owner)
            .Select(a => (Guid?)a.UserId)
            .FirstOrDefaultAsync(ct);

    public async Task SetRoleAsync(Guid userId, UserRole role, Guid? grantedBy, CancellationToken ct)
    {
        var existing = await db.ProjectRoles
            .FirstOrDefaultAsync(a => a.UserId == userId && a.ProjectKey == ProjectKey, ct);

        if (role == UserRole.User)
        {
            if (existing is not null)
                db.ProjectRoles.Remove(existing);
            return;
        }

        if (existing is null)
        {
            db.ProjectRoles.Add(new ProjectRoleAssignment
            {
                UserId = userId,
                ProjectKey = ProjectKey,
                Role = role,
                GrantedByUserId = grantedBy
            });
            return;
        }

        existing.Role = role;
        existing.GrantedByUserId = grantedBy;
        existing.GrantedAt = DateTimeOffset.UtcNow;
    }
}
