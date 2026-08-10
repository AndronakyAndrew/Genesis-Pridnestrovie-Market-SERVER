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
            // 5432/5433 на хосте заняты другими БД — БД проекта проброшена на 5434.
            ?? "Host=localhost;Port=5434;Database=genesis;Username=genesis;Password=genesis";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MapGenesisEnums())
            .Options;

        return new AppDbContext(options);
    }
}
