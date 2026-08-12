using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Api.Outbox.Telegram;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using Microsoft.Extensions.Options;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Публикация объявлений в Telegram-канал (шаг 16): пост при переходе в Active
/// (sendPhoto/sendMessage, маршрутизация «категория → канал», сохранение message_id),
/// пометки «Продано»/«Снято» правкой поста, идемпотентность повторной активации,
/// устойчивость к отсутствующему/удалённому посту.
/// </summary>
public class TelegramPublishTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    [Fact]
    public async Task Publishing_active_listing_posts_to_category_channel_and_saves_message_id()
    {
        var ownerId = await factory.SeedUserAsync(Unique("tg-pub"), Password);
        var title = Title("публикация");
        var listingId = await factory.SeedListingAsync(
            ownerId, ListingStatus.Active, title: title, category: Category.Home, subcategoryId: 18);

        await factory.EnqueueOutboxAsync(
            OutboxMessage.ListingPublished, JsonSerializer.Serialize(new { listingId }));
        var result = await factory.RunOutboxAsync();

        Assert.True(result.Delivered >= 1);

        var post = Assert.Single(factory.Telegram.Sends, s => s.Text.Contains(title));
        Assert.Equal("sendMessage", post.Method);          // без изображения — текстом
        Assert.Equal("test-home", post.ChatId);            // Home → отдельный канал категории
        Assert.Contains("https://market.test/listing/", post.Text); // абсолютная ссылка
        Assert.Contains("Дом и сад", post.Text);           // русская подпись категории

        // message_id и канал сохранены в объявлении для последующих правок.
        var (chatId, messageId) = await factory.TelegramPostAsync(listingId);
        Assert.Equal("test-home", chatId);
        Assert.Equal(post.MessageId, messageId);
    }

    [Fact]
    public async Task Publishing_with_image_uses_sendPhoto()
    {
        var ownerId = await factory.SeedUserAsync(Unique("tg-photo"), Password);
        var title = Title("с фото");
        var listingId = await factory.SeedListingAsync(ownerId, ListingStatus.Active, title: title);
        await factory.SeedListingImageAsync(listingId);

        await factory.EnqueueOutboxAsync(
            OutboxMessage.ListingPublished, JsonSerializer.Serialize(new { listingId }));
        await factory.RunOutboxAsync();

        var post = Assert.Single(factory.Telegram.Sends, s => s.Text.Contains(title));
        Assert.Equal("sendPhoto", post.Method);
        Assert.NotNull(post.PhotoUrl);
        Assert.Contains("fake-storage.local", post.PhotoUrl!);
    }

    [Fact]
    public async Task Category_without_dedicated_channel_falls_back_to_broadcast()
    {
        var ownerId = await factory.SeedUserAsync(Unique("tg-fallback"), Password);
        var title = Title("fallback");
        var listingId = await factory.SeedListingAsync(
            ownerId, ListingStatus.Active, title: title, category: Category.Other, subcategoryId: 42);

        await factory.EnqueueOutboxAsync(
            OutboxMessage.ListingPublished, JsonSerializer.Serialize(new { listingId }));
        await factory.RunOutboxAsync();

        var post = Assert.Single(factory.Telegram.Sends, s => s.Text.Contains(title));
        Assert.Equal("test-broadcast", post.ChatId); // нет канала категории → общий канал
    }

    [Fact]
    public async Task Mark_sold_edits_channel_post_with_label()
    {
        var ownerId = await factory.SeedUserAsync(Unique("tg-sold"), Password);
        var listingId = await factory.SeedListingAsync(ownerId, ListingStatus.Active, title: Title("продано"));
        await factory.SetTelegramPostAsync(listingId, "test-home", 555);

        await factory.EnqueueOutboxAsync(
            OutboxMessage.ListingChannelUpdate,
            JsonSerializer.Serialize(new { listingId, mark = ChannelMark.Sold }));
        await factory.RunOutboxAsync();

        var edit = Assert.Single(factory.Telegram.Edits, e => e.MessageId == 555);
        Assert.Equal("test-home", edit.ChatId);
        Assert.Contains("ПРОДАНО", edit.Text);
    }

    [Fact]
    public async Task Channel_update_without_post_is_delivered_without_editing()
    {
        var ownerId = await factory.SeedUserAsync(Unique("tg-nopost"), Password);
        var listingId = await factory.SeedListingAsync(ownerId, ListingStatus.Active, title: Title("без поста"));

        var editsBefore = factory.Telegram.Edits.Count;

        var id = await factory.EnqueueOutboxAsync(
            OutboxMessage.ListingChannelUpdate,
            JsonSerializer.Serialize(new { listingId, mark = ChannelMark.Archived }));
        var result = await factory.RunOutboxAsync();

        Assert.True(result.Delivered >= 1);
        var state = await factory.OutboxStateAsync(id);
        Assert.Equal(OutboxStatus.Done, state.Status); // не ошибка: поста нет — просто нечего править
        Assert.Equal(editsBefore, factory.Telegram.Edits.Count);
    }

    [Fact]
    public async Task Reactivation_edits_existing_post_instead_of_reposting()
    {
        var ownerId = await factory.SeedUserAsync(Unique("tg-react"), Password);
        var title = Title("реактивация");
        var listingId = await factory.SeedListingAsync(ownerId, ListingStatus.Active, title: title);
        await factory.SetTelegramPostAsync(listingId, "test-home", 777);

        var sendsBefore = factory.Telegram.Sends.Count;

        await factory.EnqueueOutboxAsync(
            OutboxMessage.ListingPublished, JsonSerializer.Serialize(new { listingId }));
        await factory.RunOutboxAsync();

        // Повторная публикация уже опубликованного — правка, а не новый пост.
        Assert.Equal(sendsBefore, factory.Telegram.Sends.Count);
        var edit = Assert.Single(factory.Telegram.Edits, e => e.MessageId == 777);
        Assert.DoesNotContain("ПРОДАНО", edit.Text);
        Assert.DoesNotContain("Снято", edit.Text); // чистая подпись
    }

    [Fact]
    public async Task Moderator_approval_posts_listing_to_channel()
    {
        // Полный путь перехода в Active через модерацию: approve ⇒ ListingPublished ⇒ пост в канал.
        var sellerId = await factory.SeedUserAsync(Unique("tg-approve-seller"), Password);
        var title = Title("одобрение");
        var listingId = await factory.SeedListingAsync(
            sellerId, ListingStatus.PendingReview, title: title, category: Category.Home, subcategoryId: 18);

        var mod = await ModeratorClient();
        var resp = await mod.PostAsync($"/api/moderation/listings/{listingId}/approve", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        await factory.RunOutboxAsync();

        var post = Assert.Single(factory.Telegram.Sends, s => s.Text.Contains(title));
        Assert.Equal("test-home", post.ChatId);
        var (_, messageId) = await factory.TelegramPostAsync(listingId);
        Assert.Equal(post.MessageId, messageId);
    }

    private async Task<HttpClient> ModeratorClient()
    {
        var email = Unique("tg-mod");
        await factory.SeedUserAsync(email, Password, role: UserRole.Moderator);
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";
    private static string Title(string tag) => $"Объявление TG {tag} {Guid.NewGuid():N}";
}

/// <summary>Модульные проверки скользящего лимитера частоты Telegram (без сети и БД).</summary>
public class TelegramRateLimiterTests
{
    [Fact]
    public async Task Allows_up_to_limit_then_defers_when_window_full()
    {
        var limiter = new SlidingWindowTelegramRateLimiter(Options.Create(new TelegramOptions
        {
            MaxMessagesPerMinutePerChat = 3,
            MaxRateLimitWaitMs = 0 // не ждём — переполнение сразу отклоняем
        }));

        await limiter.AcquireAsync("chat", CancellationToken.None);
        await limiter.AcquireAsync("chat", CancellationToken.None);
        await limiter.AcquireAsync("chat", CancellationToken.None);

        await Assert.ThrowsAsync<TelegramRateLimitedLocallyException>(
            () => limiter.AcquireAsync("chat", CancellationToken.None));
    }

    [Fact]
    public async Task Limit_is_independent_per_chat()
    {
        var limiter = new SlidingWindowTelegramRateLimiter(Options.Create(new TelegramOptions
        {
            MaxMessagesPerMinutePerChat = 1,
            MaxRateLimitWaitMs = 0
        }));

        await limiter.AcquireAsync("a", CancellationToken.None);
        await limiter.AcquireAsync("b", CancellationToken.None); // другой канал — свой лимит

        await Assert.ThrowsAsync<TelegramRateLimitedLocallyException>(
            () => limiter.AcquireAsync("a", CancellationToken.None));
    }
}
