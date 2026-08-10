using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Auth;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Фабрика приложения для интеграционных auth-тестов: поднимает PostgreSQL
/// в контейнере, направляет на него DbContext и прогоняет миграции.
/// </summary>
public sealed class AuthApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Строку подключения читает BuildPostgresConnectionString ещё до Build,
        // поэтому задаём её через переменную окружения (имеет приоритет).
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__Postgres", _postgres.GetConnectionString());

        // Первое обращение к Services собирает хост (с уже выставленной строкой).
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseEnvironment("Development");

    /// <summary>Прямое создание пользователя в БД (в обход register) для сценариев тестов.</summary>
    public async Task<Guid> SeedUserAsync(
        string email, string password, bool banned = false, bool phoneVerified = true)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var user = new User
        {
            Email = email.ToLowerInvariant(),
            PasswordHash = hasher.Hash(password),
            Role = UserRole.User,
            PhoneE164 = "+37312345678",
            PhoneVerified = phoneVerified,
            IsBanned = banned,
            Profile = new Profile { DisplayName = "Тестовый", City = City.Tiraspol }
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    public async Task BanUserAsync(Guid userId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsBanned, true));
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }
}
