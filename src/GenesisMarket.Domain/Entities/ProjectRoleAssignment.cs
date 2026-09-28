using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Роль пользователя в проекте: модель «пользователь + проект + роль». Сегодня проект
/// один (<see cref="Projects.Market"/>), но ключ заведён сразу, чтобы таблицу можно было
/// перенести в Genesis Ops без смены формы данных.
///
/// Строка есть только у служебных ролей: её отсутствие означает <see cref="UserRole.User"/>.
/// Поэтому таблица — это одновременно и список сотрудников. История изменений — в
/// <see cref="RoleChange"/>; эта таблица хранит только текущее состояние.
/// </summary>
public class ProjectRoleAssignment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Ключ проекта экосистемы Genesis (<see cref="Projects"/>).</summary>
    public required string ProjectKey { get; set; }

    /// <summary>Служебная роль: Moderator, Admin или Owner (CHECK в БД).</summary>
    public UserRole Role { get; set; }

    /// <summary>Кто назначил. null — назначено CLI-командой или миграцией.</summary>
    public Guid? GrantedByUserId { get; set; }

    public DateTimeOffset GrantedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Ключи проектов экосистемы Genesis.</summary>
public static class Projects
{
    /// <summary>«Местная площадка Genesis».</summary>
    public const string Market = "market";
}
