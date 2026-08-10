using GenesisMarket.Api.Auth;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Auth;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // Подменяем отправитель кодов на перехватывающий — читаем код в тесте.
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IVerificationSender>();
            services.AddSingleton<CapturingVerificationSender>();
            services.AddSingleton<IVerificationSender>(
                sp => sp.GetRequiredService<CapturingVerificationSender>());
        });
    }

    /// <summary>Последний код, «отправленный» на указанную цель (email/телефон).</summary>
    public string? LastCode(string target) =>
        Services.GetRequiredService<CapturingVerificationSender>().Last(target);

    /// <summary>Прямое создание пользователя в БД (в обход register) для сценариев тестов.</summary>
    public async Task<Guid> SeedUserAsync(
        string email, string password,
        bool banned = false, bool phoneVerified = true, bool emailVerified = false,
        UserRole role = UserRole.User)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var user = new User
        {
            Email = email.ToLowerInvariant(),
            PasswordHash = hasher.Hash(password),
            Role = role,
            PhoneE164 = "+37312345678",
            PhoneVerified = phoneVerified,
            EmailVerified = emailVerified,
            IsBanned = banned,
            Profile = new Profile { DisplayName = "Тестовый", City = City.Tiraspol }
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>Прямое создание объявления для указанного владельца (для сценариев тестов).</summary>
    public async Task<Guid> SeedListingAsync(
        Guid ownerId,
        ListingStatus status = ListingStatus.Active,
        string title = "Диван угловой тестовый",
        Category category = Category.Home,
        int subcategoryId = 18)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var published = status is ListingStatus.Active or ListingStatus.PendingReview;
        var listing = new Listing
        {
            Slug = $"seed-{Guid.NewGuid():N}",
            Title = title,
            Description = "Почти новый диван, тестовое описание объявления",
            Price = 3000,
            PriceType = PriceType.Fixed,
            Category = category,
            SubcategoryId = subcategoryId, // home/mebel из сида
            City = City.Bendery,
            Condition = Condition.Used,
            Status = status,
            PublishedAt = published ? DateTimeOffset.UtcNow : null,
            OwnerId = ownerId
        };
        db.Listings.Add(listing);
        await db.SaveChangesAsync();
        return listing.Id;
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
