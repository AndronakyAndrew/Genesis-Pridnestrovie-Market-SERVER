using GenesisMarket.Api.Auth;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Auth;
using GenesisMarket.Infrastructure.Outbox;
using GenesisMarket.Infrastructure.Persistence;
using GenesisMarket.Infrastructure.Scheduling;
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

        // Telegram-каналы для проверки публикации и маршрутизации «категория → канал».
        // Токен не задаём (реальную сеть не трогаем; клиент подменён CapturingTelegramClient).
        Environment.SetEnvironmentVariable("Telegram__BroadcastChatId", "test-broadcast");
        Environment.SetEnvironmentVariable("Telegram__CategoryChannels__home", "test-home");
        Environment.SetEnvironmentVariable("Telegram__WebBaseUrl", "https://market.test");

        // Публичный адрес сайта для SEO (canonical/sitemap/og/JSON-LD) — иначе эндпоинты отдают 503.
        Environment.SetEnvironmentVariable("Seo__WebBaseUrl", "https://market.test");

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
            // Доставку outbox гоняем вручную через RunOutboxAsync (планировщик выключен).
            RemoveHostedService<MinioBucketInitializer>(services);

            // Тестовый обработчик, всегда падающий транзиентно — для проверки ретраев/эскалации.
            services.AddScoped<GenesisMarket.Api.Outbox.IOutboxHandler, TestFailingOutboxHandler>();

            // Telegram-клиент подменяем перехватывающим — проверяем посты/правки без сети.
            services.RemoveAll<GenesisMarket.Api.Outbox.Telegram.ITelegramClient>();
            services.AddSingleton<CapturingTelegramClient>();
            services.AddSingleton<GenesisMarket.Api.Outbox.Telegram.ITelegramClient>(
                sp => sp.GetRequiredService<CapturingTelegramClient>());
        });
    }

    /// <summary>Доступ к фейковому хранилищу для проверок в тестах загрузки фото.</summary>
    public FakeObjectStorage Storage => Services.GetRequiredService<FakeObjectStorage>();

    /// <summary>Перехваченные вызовы Telegram (посты/правки) для проверок публикации в канал.</summary>
    public CapturingTelegramClient Telegram => Services.GetRequiredService<CapturingTelegramClient>();

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

    /// <summary>Добавляет объявлению изображение (только строку БД — для проверки sendPhoto).</summary>
    public async Task SeedListingImageAsync(Guid listingId, int sortOrder = 0)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = $"listings/{listingId}/{Guid.NewGuid():N}.webp";
        db.ListingImages.Add(new ListingImage
        {
            ListingId = listingId,
            ObjectKey = key,
            ThumbKey = key.Replace(".webp", "_thumb.webp"),
            SortOrder = sortOrder,
            Width = 800,
            Height = 600
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Проставляет объявлению координаты поста в Telegram (эмуляция уже опубликованного).</summary>
    public async Task SetTelegramPostAsync(Guid listingId, string chatId, long messageId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Listings.IgnoreQueryFilters()
            .Where(l => l.Id == listingId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.TelegramChatId, chatId)
                .SetProperty(l => l.TelegramMessageId, messageId));
    }

    /// <summary>Координаты поста объявления в Telegram-канале (после публикации).</summary>
    public async Task<(string? ChatId, long? MessageId)> TelegramPostAsync(Guid listingId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var l = await db.Listings.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == listingId)
            .Select(x => new { x.TelegramChatId, x.TelegramMessageId })
            .FirstAsync();
        return (l.TelegramChatId, l.TelegramMessageId);
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

    /// <summary>Пишет факт раскрытия контактов (гейт для отзыва) напрямую в журнал.</summary>
    public async Task SeedContactRevealAsync(Guid listingId, Guid viewerUserId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ContactReveals.Add(new ContactReveal
        {
            ListingId = listingId,
            ViewerUserId = viewerUserId,
            IpHash = "seed-iphash"
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Денормализованный агрегат отзывов продавца (для проверки триггера).</summary>
    public async Task<(double? Average, int Count)> UserRatingAsync(Guid userId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var u = await db.Users.AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new { x.AverageRating, x.ReviewsCount })
            .FirstAsync();
        return (u.AverageRating, u.ReviewsCount);
    }

    /// <summary>Текущий статус и приоритет очереди модерации объявления (для проверки автоматики).</summary>
    public async Task<(string Status, int ModerationPriority)> ListingModerationAsync(Guid listingId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var l = await db.Listings.IgnoreQueryFilters()
            .Where(x => x.Id == listingId)
            .Select(x => new { x.Status, x.ModerationPriority })
            .FirstAsync();
        return (l.Status.ToString(), l.ModerationPriority);
    }

    /// <summary>Сколько жалоб записано на объект (для проверки дедупликации).</summary>
    public async Task<int> ReportCountAsync(Guid targetId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Reports.CountAsync(r => r.TargetId == targetId);
    }

    /// <summary>Денормализованный счётчик избранного у объявления (для проверки триггера).</summary>
    public async Task<int> FavoritesCountAsync(Guid listingId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Listings.IgnoreQueryFilters()
            .Where(l => l.Id == listingId).Select(l => l.FavoritesCount).FirstAsync();
    }

    /// <summary>Мягко удаляет объявление (проставляет DeletedAt) — для проверки 410 Gone в SEO-мета.</summary>
    public async Task SoftDeleteListingAsync(Guid listingId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Listings.IgnoreQueryFilters()
            .Where(l => l.Id == listingId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.DeletedAt, DateTimeOffset.UtcNow));
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

    /// <summary>Прямое создание жалобы на объект (для наполнения очереди модерации).</summary>
    public async Task<Guid> SeedReportAsync(
        Guid targetId,
        ReportTargetType targetType = ReportTargetType.Listing,
        ReportReason reason = ReportReason.Spam,
        Guid? reporterId = null,
        string? comment = "Тестовая жалоба")
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var report = new Report
        {
            TargetType = targetType,
            TargetId = targetId,
            ReporterId = reporterId,
            Reason = reason,
            Comment = comment
        };
        db.Reports.Add(report);
        await db.SaveChangesAsync();
        return report.Id;
    }

    /// <summary>Сколько записей журнала модерации по объекту (опционально по коду действия).</summary>
    public async Task<int> ModerationLogCountAsync(Guid targetId, string? action = null)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var q = db.ModerationLogs.Where(m => m.TargetId == targetId);
        if (action is not null)
            q = q.Where(m => m.Action == action);
        return await q.CountAsync();
    }

    /// <summary>Поднимает приоритет очереди модерации напрямую (эмуляция автофлага для теста сортировки).</summary>
    public async Task SetListingPriorityAsync(Guid listingId, int priority)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Listings
            .Where(l => l.Id == listingId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.ModerationPriority, priority));
    }

    /// <summary>Забанен ли пользователь (для проверки транзакции бана).</summary>
    public async Task<(bool IsBanned, DateTimeOffset? Until)> UserBanStateAsync(Guid userId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var u = await db.Users.AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new { x.IsBanned, x.BannedUntil })
            .FirstAsync();
        return (u.IsBanned, u.BannedUntil);
    }

    /// <summary>Сколько сообщений outbox указанного типа ссылается на id (по JSON payload).</summary>
    public async Task<int> OutboxCountAsync(string type, Guid id)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var needle = id.ToString();
        return await db.OutboxMessages
            .CountAsync(m => m.Type == type && m.Payload.Contains(needle));
    }

    /// <summary>Прогоняет один тик диспетчера outbox напрямую (планировщик в тестах выключен).</summary>
    public async Task<OutboxDispatchResult> RunOutboxAsync()
    {
        using var scope = Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
        return await dispatcher.DispatchOnceAsync(CancellationToken.None);
    }

    /// <summary>Кладёт сообщение в outbox напрямую (для сценариев диспетчера); возвращает его Id.</summary>
    public async Task<Guid> EnqueueOutboxAsync(string type, string payload)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var message = new OutboxMessage { Type = type, Payload = payload };
        db.OutboxMessages.Add(message);
        await db.SaveChangesAsync();
        return message.Id;
    }

    /// <summary>Состояние сообщения outbox (для проверок ретраев/финализации).</summary>
    public async Task<(OutboxStatus Status, int Attempts, string? Error, DateTimeOffset NextAttemptAt)> OutboxStateAsync(Guid id)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var m = await db.OutboxMessages.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.Status, x.Attempts, x.Error, x.NextAttemptAt })
            .FirstAsync();
        return (m.Status, m.Attempts, m.Error, m.NextAttemptAt);
    }

    /// <summary>Делает сообщение готовым к немедленной попытке (сдвигает NextAttemptAt в прошлое).</summary>
    public async Task MakeOutboxDueAsync(Guid id)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.OutboxMessages
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
    }

    public async Task BanUserAsync(Guid userId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsBanned, true));
    }

    /// <summary>Прогоняет джоб гигиены каталога напрямую (в тестах планировщик выключен).</summary>
    public async Task<HygieneResult> RunCatalogHygieneAsync()
    {
        using var scope = Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ICatalogHygieneService>();
        return await svc.RunAsync(CancellationToken.None);
    }

    /// <summary>Прогоняет джоб рассылки сохранённых поисков напрямую (планировщик в тестах выключен).</summary>
    public async Task<SavedSearchNotificationResult> RunSavedSearchNotificationsAsync()
    {
        using var scope = Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ISavedSearchNotificationService>();
        return await svc.RunAsync(CancellationToken.None);
    }

    /// <summary>Сдвигает NotifiedAt поиска (для проверки часового гейта и независимости курсора).</summary>
    public async Task SetSavedSearchNotifiedAtAsync(Guid id, DateTimeOffset? when)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.SavedSearches
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NotifiedAt, when));
    }

    /// <summary>Состояние сохранённого поиска (курсор/уведомление/активность).</summary>
    public async Task<(Guid? LastNotifiedListingId, DateTimeOffset? NotifiedAt, bool IsActive)> SavedSearchStateAsync(Guid id)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var s = await db.SavedSearches.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.LastNotifiedListingId, x.NotifiedAt, x.IsActive })
            .FirstAsync();
        return (s.LastNotifiedListingId, s.NotifiedAt, s.IsActive);
    }

    /// <summary>Портит QueryJson поиска (для проверки, что джоб не доверяет содержимому jsonb).</summary>
    public async Task SetSavedSearchQueryJsonAsync(Guid id, string json)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.SavedSearches
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.QueryJson, json));
    }

    /// <summary>Сдвигает «возраст» объявления: BumpedAt/PublishedAt напрямую (для проверки сроков).</summary>
    public async Task SetBumpTimestampsAsync(Guid listingId, DateTimeOffset? bumpedAt, DateTimeOffset? publishedAt = null)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Listings
            .Where(l => l.Id == listingId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.BumpedAt, bumpedAt)
                .SetProperty(l => l.PublishedAt, publishedAt ?? bumpedAt));
    }

    /// <summary>Задаёт SoldAt напрямую (для проверки окна реактивации).</summary>
    public async Task SetSoldAtAsync(Guid listingId, DateTimeOffset soldAt)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Listings
            .Where(l => l.Id == listingId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.SoldAt, soldAt));
    }

    /// <summary>Задаёт ArchivedAt напрямую (для проверки окна восстановления).</summary>
    public async Task SetArchivedAtAsync(Guid listingId, DateTimeOffset archivedAt)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Listings
            .Where(l => l.Id == listingId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.ArchivedAt, archivedAt));
    }

    /// <summary>Пишет запись журнала модерации о reject объявления (для проверки повторной премодерации).</summary>
    public async Task SeedRejectLogAsync(Guid listingId, Guid moderatorId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ModerationLogs.Add(new ModerationLog
        {
            ActorId = moderatorId,
            Action = ModerationLog.ActionRejectListing,
            TargetType = ModerationLog.TargetListing,
            TargetId = listingId
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Полное состояние жизненного цикла объявления (для проверки переходов и джоба).</summary>
    public async Task<(string Status, DateTimeOffset? BumpedAt, DateTimeOffset? ArchivedAt,
        DateTimeOffset? SoldAt, DateTimeOffset? ArchiveWarningAt)> LifecycleStateAsync(Guid listingId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var l = await db.Listings.IgnoreQueryFilters()
            .Where(x => x.Id == listingId)
            .Select(x => new { x.Status, x.BumpedAt, x.ArchivedAt, x.SoldAt, x.ArchiveWarningAt })
            .FirstAsync();
        return (l.Status.ToString(), l.BumpedAt, l.ArchivedAt, l.SoldAt, l.ArchiveWarningAt);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }
}
