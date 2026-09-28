namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Общий журнал действий сотрудников: кто, когда, что сделал, над каким объектом,
/// что было и что стало. Таблица только на добавление — UPDATE и DELETE запрещены
/// триггером БД (кроме снятия <see cref="IpHash"/> по сроку хранения).
///
/// В отличие от <see cref="ModerationLog"/> (рабочий журнал модераторов, по нему
/// считается статистика очереди), сюда попадают действия всех уровней: решения
/// модерации (зеркалом из <c>IModerationAudit</c>), баны, справочники, роли, 2FA,
/// а также действия CLI, у которых нет пользователя-исполнителя.
/// </summary>
public class AuditEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Проект экосистемы (<see cref="Projects"/>).</summary>
    public required string ProjectKey { get; set; }

    /// <summary>Исполнитель. null — действие CLI или системы (см. <see cref="ActorKind"/>).</summary>
    public Guid? ActorId { get; set; }

    /// <summary><see cref="ActorUser"/>, <see cref="ActorCli"/> или <see cref="ActorSystem"/>.</summary>
    public required string ActorKind { get; set; }

    /// <summary>Для CLI — «пользователь ОС@машина»: другого следа у консольного вызова нет.</summary>
    public string? ActorLabel { get; set; }

    /// <summary>Код действия, например <c>role.change</c> или <c>listing.approve</c>.</summary>
    public required string Action { get; set; }

    /// <summary>Тип объекта: <c>user</c>, <c>listing</c>, <c>city</c>, …</summary>
    public required string TargetType { get; set; }

    /// <summary>
    /// Идентификатор объекта строкой: у пользователей и объявлений это Guid, у
    /// подкатегорий — число, у городов и категорий — код.
    /// </summary>
    public required string TargetId { get; set; }

    /// <summary>Состояние до действия (jsonb). null — объекта не было или действие не меняет состояние.</summary>
    public string? OldValue { get; set; }

    /// <summary>Состояние после действия (jsonb).</summary>
    public string? NewValue { get; set; }

    /// <summary>Причина, указанная исполнителем. До 500 символов.</summary>
    public string? Reason { get; set; }

    /// <summary>Идентификатор HTTP-запроса — связывает запись с логами.</summary>
    public string? RequestId { get; set; }

    /// <summary>HMAC адреса исполнителя. Снимается по сроку хранения (JournalRetention).</summary>
    public string? IpHash { get; set; }

    public const string ActorUser = "user";
    public const string ActorCli = "cli";
    public const string ActorSystem = "system";
}

/// <summary>
/// История изменений ролей — отдельно от общего журнала: это самые чувствительные
/// действия, и по ним нужен ответ на вопрос «кто и когда дал этому человеку права»
/// без фильтрации общего потока. Только на добавление (триггер БД).
/// </summary>
public class RoleChange
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    public required string ProjectKey { get; set; }

    /// <summary>Чья роль изменилась. Без FK: история переживает удаление аккаунта.</summary>
    public Guid UserId { get; set; }

    /// <summary>Роль до изменения (User — строки назначения не было).</summary>
    public required string OldRole { get; set; }

    public required string NewRole { get; set; }

    /// <summary>Кто изменил. null — CLI.</summary>
    public Guid? ActorId { get; set; }

    /// <summary><see cref="SourceUi"/> или <see cref="SourceCli"/>.</summary>
    public required string Source { get; set; }

    /// <summary>Для CLI — «пользователь ОС@машина».</summary>
    public string? ActorLabel { get; set; }

    public string? Reason { get; set; }

    /// <summary>Парная запись общего журнала.</summary>
    public Guid AuditEventId { get; set; }

    public const string SourceUi = "ui";
    public const string SourceCli = "cli";
}
