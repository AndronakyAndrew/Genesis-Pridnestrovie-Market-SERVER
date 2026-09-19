using Npgsql;

namespace GenesisMarket.Api.Business;

internal static class DbExceptions
{
    /// <summary>
    /// Нарушение уникальности (23505). SaveChanges заворачивает PostgresException
    /// в DbUpdateException, ExecuteUpdate — бросает его как есть; проверяем оба.
    /// </summary>
    public static bool IsUniqueViolation(this Exception ex) =>
        (ex as PostgresException ?? ex.InnerException as PostgresException) is { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>Нарушение CHECK-констрейнта (23514) — в любой из двух обёрток.</summary>
    public static bool IsCheckViolation(this Exception ex) =>
        (ex as PostgresException ?? ex.InnerException as PostgresException) is { SqlState: PostgresErrorCodes.CheckViolation };
}
