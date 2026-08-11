using GenesisMarket.Api.Auth;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Auth;
using GenesisMarket.Infrastructure.Persistence;
using GenesisMarket.Infrastructure.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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

        // Ключ HMAC для хеширования IP — чтобы раскрытие контактов писало IpHash, а не сырой IP.
        Environment.SetEnvironmentVariable(
            "Security__IpHashKey", "test-iphash-key-0123456789abcdef0123456789abcdef");

        // Задержку анонимам в тестах убираем — проверяем логику, а не тайминг.
        Environment.SetEnvironmentVariable("ContactReveal__MinDelayMs", "0");
        Environment.SetEnvironmentVariable("ContactReveal__MaxDelayMs", "0");

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

            // MinIO в тестах не поднимаем: подменяем хранилище in-memory реализацией.
            services.RemoveAll<IObjectStorage>();
            services.AddSingleton<FakeObjectStorage>();
            services.AddSingleton<IObjectStorage>(sp => sp.GetRequiredService<FakeObjectStorage>());

            // Фоновые сервисы, которым нужен реальный MinIO, в тестах не запускаем.
            RemoveHostedService<MinioBucketInitializer>(services);
            RemoveHostedService<ObjectDeletionOutboxProcessor>(services);
        });
    }

    /// <summary>Доступ к фейковому хранилищу для проверок в тестах загрузки фото.</summary>
    public FakeObjectStorage Storage => Services.GetRequiredService<FakeObjectStorage>();

    private static void RemoveHostedService<T>(IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(
            s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(T));
        if (descriptor is not null)
            services.Remove(descriptor);
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
        int subcategoryId = 18,
        decimal? price = 3000,
        PriceType priceType = PriceType.Fixed,
        City city = City.Bendery,
        string? description = null)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var published = status is ListingStatus.Active or ListingStatus.PendingReview;
        var listing = new Listing
        {
            Slug = $"seed-{Guid.NewGuid():N}",
            Title = title,
            Description = description ?? "Почти новый диван, тестовое описание объявления",
            Price = price,
            PriceType = priceType,
            Category = category,
            SubcategoryId = subcategoryId, // home/mebel из сида
            City = city,
            Condition = Condition.Used,
            Status = status,
            PublishedAt = published ? DateTimeOffset.UtcNow : null,
            OwnerId = ownerId
        };
        db.Listings.Add(listing);
        await db.SaveChangesAsync();
        return listing.Id;
    }

    /// <summary>Выставляет счётчик просмотров напрямую (для сортировки popular).</summary>
    public async Task SetViewsAsync(Guid listingId, int views)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Listings
            .Where(l => l.Id == listingId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.ViewsCount, views));
    }

    /// <summary>Сколько раз данный запрос попал в SearchMisses (нулевая выдача).</summary>
    public async Task<int> SearchMissCountAsync(string query)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SearchMisses.CountAsync(m => m.Query == query);
    }

    /// <summary>Настройка контактных каналов продавца (для тестов раскрытия контактов).</summary>
    public async Task ConfigureContactAsync(
        Guid userId, bool showPhone = true,
        string? telegram = null, bool viber = false, bool whatsapp = false)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Profiles
            .Where(p => p.UserId == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.ShowPhoneInListing, showPhone)
                .SetProperty(p => p.TelegramUsername, telegram)
                .SetProperty(p => p.ViberEnabled, viber)
                .SetProperty(p => p.WhatsappEnabled, whatsapp));
    }

    /// <summary>Сколько раскрытий контактов записано по объявлению.</summary>
    public async Task<int> ContactRevealCountAsync(Guid listingId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ContactReveals.CountAsync(r => r.ListingId == listingId);
    }

    /// <summary>Денормализованный счётчик избранного у объявления (для проверки триггера).</summary>
    public async Task<int> FavoritesCountAsync(Guid listingId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Listings.IgnoreQueryFilters()
            .Where(l => l.Id == listingId).Select(l => l.FavoritesCount).FirstAsync();
    }

    /// <summary>Меняет статус объявления напрямую (для проверки isUnavailable в избранном).</summary>
    public async Task SetStatusAsync(Guid listingId, ListingStatus status)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Listings
            .Where(l => l.Id == listingId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, status));
    }

    /// <summary>
    /// Массово наполняет избранное пользователя (count чужих объявлений + count записей).
    /// Для проверки лимита без 500 HTTP-вызовов. Триггер счётчика при этом отрабатывает.
    /// </summary>
    public async Task SeedManyFavoritesAsync(Guid userId, Guid otherOwnerId, int count)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        for (var i = 0; i < count; i++)
        {
            var listing = new Listing
            {
                Slug = $"seed-fav-{Guid.NewGuid():N}",
                Title = "Объявление для лимита избранного",
                Description = "Тестовое описание для наполнения избранного",
                Price = 100,
                PriceType = PriceType.Fixed,
                Category = Category.Other,
                SubcategoryId = 42,
                City = City.Tiraspol,
                Condition = Condition.Used,
                Status = ListingStatus.Active,
                PublishedAt = DateTimeOffset.UtcNow,
                OwnerId = otherOwnerId
            };
            db.Listings.Add(listing);
            db.Favorites.Add(new Favorite { UserId = userId, ListingId = listing.Id });
        }
        await db.SaveChangesAsync();
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
