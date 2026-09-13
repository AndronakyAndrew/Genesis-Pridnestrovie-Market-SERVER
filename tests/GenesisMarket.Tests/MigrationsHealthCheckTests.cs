using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Регрессия на реальный инцидент: код уехал вперёд неприменённых миграций, и это
/// проявилось не сигналом готовности, а пятисоткой в /api/auth/login —
/// <c>42703: column "BrowserFamily" of relation "refresh_tokens" does not exist</c>.
/// Миграции при старте намеренно не накатываются, поэтому рассинхрон возможен
/// всегда; задача проверки — сделать его видимым до того, как в него упрётся
/// пользователь.
/// </summary>
public sealed class MigrationsHealthCheckTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Reports_unhealthy_while_migrations_are_pending_and_healthy_after_they_are_applied()
    {
        await using var db = NewContext();

        // Схема на состоянии «до сессий»: колонок BrowserFamily/SessionId ещё нет,
        // ровно как было в dev-базе в момент инцидента.
        await db.GetService<IMigrator>()
            .MigrateAsync("20260913112542_AddUserPublicCode");

        Assert.NotEmpty(await db.Database.GetPendingMigrationsAsync());

        var check = new MigrationsHealthCheck(db, NullLogger<MigrationsHealthCheck>.Instance);
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("migrations", check, HealthStatus.Unhealthy, ["ready"])
        };

        var behind = await check.CheckHealthAsync(context);
        Assert.Equal(HealthStatus.Unhealthy, behind.Status);
        // Наружу — только сводная формулировка, без имён миграций и таблиц.
        Assert.DoesNotContain("refresh_tokens", behind.Description);
        Assert.DoesNotContain("20260913", behind.Description);

        // Схему догнали — готовность восстановилась.
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        var caughtUp = await check.CheckHealthAsync(context);
        Assert.Equal(HealthStatus.Healthy, caughtUp.Status);
    }

    private AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.MapGenesisEnums())
            .Options);
}
