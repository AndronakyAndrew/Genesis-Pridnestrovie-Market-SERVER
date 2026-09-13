using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace GenesisMarket.Infrastructure.Persistence;

/// <summary>
/// Готовность схемы: приложение не должно считаться готовым, если код ушёл
/// вперёд неприменённых миграций.
///
/// Миграции при старте намеренно не накатываются (роль приложения не имеет прав
/// на DDL, схему меняет отдельный шаг деплоя), поэтому рассинхрон возможен — и
/// раньше он проявлялся случайной пятисоткой в первой же ручке, которая тронет
/// новую колонку: <c>42703: column "…" does not exist</c> где-нибудь в логине.
/// Теперь это видно сразу на <c>/health/ready</c>, то есть ломается smoke-тест
/// деплоя, а не пользователь.
///
/// Наружу уходит только сводный статус (эндпоинт анонимный); имена миграций —
/// в лог, где им и место.
/// </summary>
public sealed class MigrationsHealthCheck(
    AppDbContext db,
    ILogger<MigrationsHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        // Без кеширования результата: это один SELECT из __EFMigrationsHistory
        // (таблица в десятки строк) на пробу готовности, которая и так ходит в
        // Postgres проверкой рядом. Зато проверка остаётся честной — миграции
        // могут накатить, пока процесс живёт, и наоборот.
        List<string> pending;
        try
        {
            pending = [.. await db.Database.GetPendingMigrationsAsync(ct)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Недоступность самой БД — забота проверки postgres, не этой:
            // не дублируем её диагноз, но и готовыми себя не объявляем.
            return HealthCheckResult.Unhealthy("Не удалось проверить версию схемы базы данных.");
        }

        if (pending.Count == 0)
            return HealthCheckResult.Healthy();

        logger.LogError(
            "Схема базы данных отстаёт от кода: не применено миграций — {Count} ({Pending}). " +
            "Выполните шаг деплоя scripts/migrate.sh.",
            pending.Count, string.Join(", ", pending));

        return HealthCheckResult.Unhealthy("Схема базы данных не соответствует версии приложения.");
    }
}
