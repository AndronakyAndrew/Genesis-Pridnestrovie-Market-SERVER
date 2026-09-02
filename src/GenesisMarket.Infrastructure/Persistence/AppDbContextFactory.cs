using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GenesisMarket.Infrastructure.Persistence;

/// <summary>
/// Design-time фабрика для инструментов EF Core (dotnet ef migrations add ...).
/// Строку подключения берёт из GENESIS_DESIGN_CONNECTION либо использует заглушку —
/// при создании миграции реальное подключение к БД не требуется.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connection =
            Environment.GetEnvironmentVariable("GENESIS_DESIGN_CONNECTION")
            // Заглушка нужна только для `migrations add` (модель строится без реального
            // подключения). Хост заведомо нерезолвящийся — если кто-то по ошибке запустит
            // `database update` без явного GENESIS_DESIGN_CONNECTION, команда упадёт с понятной
            // ошибкой DNS, а не тихо накатит миграцию на первую попавшуюся БД на localhost
            // (напр. прод, если её порт проброшен на этом же хосте).
            ?? "Host=set-GENESIS_DESIGN_CONNECTION-env-var.invalid;Port=5432;Database=genesis;Username=genesis;Password=genesis";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MapGenesisEnums())
            .Options;

        return new AppDbContext(options);
    }
}
