using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using GenesisMarket.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Testcontainers.PostgreSql;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Проверяет РЕАЛЬНУЮ обвязку планировщика (в остальных тестах он выключен): Quartz
/// с persistent job store в PostgreSQL инициализируется на нашей схеме (таблицы qrtz_*
/// из миграции), джоб зарегистрирован и по ручному триггеру действительно выполняет
/// гигиену каталога. Это тот путь, что запускается на старте приложения в проде.
/// </summary>
public sealed class SchedulerIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var conn = _postgres.GetConnectionString();

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Scheduling:Enabled"] = "true",
            // Раз в год — чтобы автотриггер не сработал во время теста; джоб дёргаем вручную.
            ["Scheduling:HygieneCron"] = "0 0 3 1 1 ? 2099"
        });
        builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(conn, npgsql => npgsql.MapGenesisEnums()));
        builder.Services.AddScheduling(builder.Configuration, conn);

        _host = builder.Build();

        // Миграции (в т.ч. таблицы qrtz_*) должны существовать до старта планировщика.
        using (var scope = _host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }

    [Fact]
    public async Task Quartz_persistent_store_starts_and_runs_hygiene_job()
    {
        var listingId = await SeedExpiredActiveListingAsync();

        // StartAsync поднимает hosted-сервисы, включая QuartzHostedService (инициализация стора).
        await _host.StartAsync();
        try
        {
            var scheduler = await _host.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();

            // Планировщик стартует после события ApplicationStarted (асинхронно) — дожидаемся.
            var started = await WaitAsync(() => Task.FromResult(scheduler.IsStarted), TimeSpan.FromSeconds(15));
            Assert.True(started, "Планировщик Quartz не запустился");
            Assert.True(await scheduler.CheckExists(CatalogHygieneJob.Key));

            await scheduler.TriggerJob(CatalogHygieneJob.Key);

            var archived = await WaitUntilArchivedAsync(listingId, TimeSpan.FromSeconds(30));
            Assert.True(archived, "Джоб гигиены не заархивировал просроченное объявление за отведённое время");
        }
        finally
        {
            await _host.StopAsync();
        }
    }

    private async Task<Guid> SeedExpiredActiveListingAsync()
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = new User
        {
            Email = $"sched-{Guid.NewGuid():N}@test.io",
            PasswordHash = "x",
            PhoneE164 = "+37312345678",
            Profile = new Profile { DisplayName = "Планировщик", City = City.Tiraspol }
        };
        db.Users.Add(user);

        var old = DateTimeOffset.UtcNow.AddDays(-40);
        var listing = new Listing
        {
            Slug = $"sched-{Guid.NewGuid():N}",
            Title = "Просроченное объявление планировщика",
            Description = "Тестовое объявление для проверки автоархивации джобом",
            Price = 100,
            PriceType = PriceType.Fixed,
            Category = Category.Other,
            SubcategoryId = 42,
            City = City.Tiraspol,
            Condition = Condition.Used,
            Status = ListingStatus.Active,
            PublishedAt = old,
            OwnerId = user.Id
        };
        db.Listings.Add(listing);
        await db.SaveChangesAsync();
        return listing.Id;
    }

    private async Task<bool> WaitUntilArchivedAsync(Guid listingId, TimeSpan timeout) =>
        await WaitAsync(async () =>
        {
            using var scope = _host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var status = await db.Listings.IgnoreQueryFilters()
                .Where(l => l.Id == listingId).Select(l => l.Status).FirstAsync();
            return status == ListingStatus.Archived;
        }, timeout);

    private static async Task<bool> WaitAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return true;
            await Task.Delay(500);
        }
        return false;
    }

    public async Task DisposeAsync()
    {
        _host.Dispose();
        await _postgres.DisposeAsync();
    }
}
