using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Api.Outbox.Telegram;
using GenesisMarket.Api.Telegram.Channel;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using Microsoft.Extensions.Options;
using Xunit;
using BotApi = GenesisMarket.Api.Telegram;

namespace GenesisMarket.Tests;

/// <summary>
/// Публикация объявлений в Telegram-канал через очередь: постановка при одобрении, тик воркера
/// (фото, подпись, кнопка, message_id), пауза между постами, рабочее окно, ошибки и повторы,
/// правки поста «Продано»/«Снято»/возврат подписи через outbox. Нужен Docker (PostgreSQL).
/// </summary>
public class TelegramPublishTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    // Окно в тестах считается в UTC (см. AuthApiFactory): полдень — внутри 09:00–21:00.
    private static readonly DateTimeOffset Noon = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Moderator_approval_enqueues_post_without_sending_immediately()
    {
        await factory.ClearChannelQueueAsync();
        var sellerId = await factory.SeedUserAsync(Unique("tg-approve"), Password);
        var listingId = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview, title: Title("одобрение"));

        var resp = await (await ModeratorClient()).PostAsync($"/api/moderation/listings/{listingId}/approve", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        await factory.RunOutboxAsync();

        var item = await factory.ChannelQueueItemAsync(listingId);
        Assert.NotNull(item);
        Assert.Equal(ChannelPostStatus.Pending, item.Status);
        Assert.Empty(factory.Bot.PhotosFor(listingId)); // в канал уходит только воркером
    }

    [Fact]
    public async Task Post_moderation_listing_is_enqueued_only_after_approval()
    {
        await factory.ClearChannelQueueAsync();
        var sellerId = await factory.SeedUserAsync(Unique("tg-postmod"), Password);
        var listingId = await factory.SeedListingAsync(sellerId, ListingStatus.Active, title: Title("постмодерация"));
        await factory.QueueForPostReviewAsync(listingId);

        Assert.Null(await factory.ChannelQueueItemAsync(listingId));

        var resp = await (await ModeratorClient()).PostAsync($"/api/moderation/listings/{listingId}/approve", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Equal(ChannelPostStatus.Pending, (await factory.ChannelQueueItemAsync(listingId))?.Status);
    }

    [Fact]
    public async Task Tick_publishes_photo_with_caption_and_redirect_button_and_saves_message_id()
    {
        await factory.ClearChannelQueueAsync();
        var listingId = await SeedPublishableListingAsync("публикация");
        await factory.EnqueueChannelPostAsync(listingId);

        Assert.Equal(ChannelPublishOutcome.Published, await factory.PublishNextChannelPostAsync(Noon));

        var post = Assert.Single(factory.Bot.PhotosFor(listingId));
        Assert.Equal("@test_channel", post.ChatId);
        Assert.StartsWith($"https://api.test/api/images/listings/{listingId}/", post.PhotoUrl);
        Assert.Equal(ChannelPostFormatter.ButtonText, post.Button!.Text);
        Assert.Equal($"https://api.test/r/l/{listingId}?s=tg", post.Button.Url);
        Assert.StartsWith("📦 #объявления #Бендеры", post.Caption);
        Assert.Contains("💰 3 000 руб.", post.Caption);

        var item = await factory.ChannelQueueItemAsync(listingId);
        Assert.Equal(ChannelPostStatus.Published, item!.Status);
        Assert.Equal(Noon, item.PublishedAt);

        var (chatId, messageId) = await factory.TelegramPostAsync(listingId);
        Assert.Equal("@test_channel", chatId);
        Assert.Equal(post.MessageId, messageId);
        Assert.Equal(Noon, await factory.TelegramPublishedAtAsync(listingId));
    }

    [Fact]
    public async Task Next_post_waits_for_min_interval_and_takes_oldest_first()
    {
        await factory.ClearChannelQueueAsync();
        var first = await SeedPublishableListingAsync("первое");
        var second = await SeedPublishableListingAsync("второе");
        await factory.EnqueueChannelPostAsync(first);
        await factory.EnqueueChannelPostAsync(second);

        Assert.Equal(ChannelPublishOutcome.Published, await factory.PublishNextChannelPostAsync(Noon));
        Assert.Single(factory.Bot.PhotosFor(first));

        Assert.Equal(ChannelPublishOutcome.TooSoon, await factory.PublishNextChannelPostAsync(Noon.AddMinutes(19)));
        Assert.Empty(factory.Bot.PhotosFor(second));

        Assert.Equal(ChannelPublishOutcome.Published, await factory.PublishNextChannelPostAsync(Noon.AddMinutes(20)));
        Assert.Single(factory.Bot.PhotosFor(second));
    }

    [Theory]
    [InlineData(8, 59)]
    [InlineData(21, 0)]
    [InlineData(3, 0)]
    public async Task Nothing_is_sent_outside_window(int hour, int minute)
    {
        await factory.ClearChannelQueueAsync();
        var listingId = await SeedPublishableListingAsync($"окно {hour}:{minute}");
        await factory.EnqueueChannelPostAsync(listingId);

        var at = new DateTimeOffset(2026, 9, 15, hour, minute, 0, TimeSpan.Zero);
        Assert.Equal(ChannelPublishOutcome.OutsideWindow, await factory.PublishNextChannelPostAsync(at));

        Assert.Equal(ChannelPostStatus.Pending, (await factory.ChannelQueueItemAsync(listingId))!.Status);
        Assert.Empty(factory.Bot.PhotosFor(listingId));
    }

    [Fact]
    public async Task Bad_request_fails_immediately_without_retry()
    {
        await factory.ClearChannelQueueAsync();
        var listingId = await SeedPublishableListingAsync("битое фото");
        await factory.EnqueueChannelPostAsync(listingId);
        factory.Bot.FailNextSend(new BotApi.TelegramApiException(400, "Bad Request: wrong type of the web page content"));

        Assert.Equal(ChannelPublishOutcome.Failed, await factory.PublishNextChannelPostAsync(Noon));

        var item = await factory.ChannelQueueItemAsync(listingId);
        Assert.Equal(ChannelPostStatus.Failed, item!.Status);
        Assert.Equal(1, item.AttemptCount);
        Assert.Contains("400", item.LastError);
    }

    [Fact]
    public async Task Transient_error_is_retried_and_fails_after_max_attempts()
    {
        await factory.ClearChannelQueueAsync();
        var listingId = await SeedPublishableListingAsync("сеть");
        await factory.EnqueueChannelPostAsync(listingId);
        for (var i = 0; i < 3; i++)
            factory.Bot.FailNextSend(new HttpRequestException("сеть недоступна"));

        Assert.Equal(ChannelPublishOutcome.Retrying, await factory.PublishNextChannelPostAsync(Noon));
        Assert.Equal(ChannelPublishOutcome.Retrying, await factory.PublishNextChannelPostAsync(Noon.AddMinutes(1)));
        Assert.Equal(ChannelPublishOutcome.Failed, await factory.PublishNextChannelPostAsync(Noon.AddMinutes(2)));

        var item = await factory.ChannelQueueItemAsync(listingId);
        Assert.Equal(ChannelPostStatus.Failed, item!.Status);
        Assert.Equal(3, item.AttemptCount);
        Assert.Contains("сеть недоступна", item.LastError);
    }

    [Fact]
    public async Task Listing_without_photo_is_skipped()
    {
        await factory.ClearChannelQueueAsync();
        var ownerId = await factory.SeedUserAsync(Unique("tg-nophoto"), Password);
        var listingId = await factory.SeedListingAsync(ownerId, ListingStatus.Active, title: Title("без фото"));
        await factory.EnqueueChannelPostAsync(listingId);

        Assert.Equal(ChannelPublishOutcome.Skipped, await factory.PublishNextChannelPostAsync(Noon));
        Assert.Equal(ChannelPostStatus.Skipped, (await factory.ChannelQueueItemAsync(listingId))!.Status);
    }

    [Fact]
    public async Task Listing_sold_while_waiting_is_skipped()
    {
        await factory.ClearChannelQueueAsync();
        var listingId = await SeedPublishableListingAsync("продано в очереди");
        await factory.EnqueueChannelPostAsync(listingId);
        await factory.SetStatusAsync(listingId, ListingStatus.Sold);

        Assert.Equal(ChannelPublishOutcome.Skipped, await factory.PublishNextChannelPostAsync(Noon));
        Assert.Empty(factory.Bot.PhotosFor(listingId));
    }

    [Fact]
    public async Task Mark_sold_keeps_title_adds_label_and_removes_button()
    {
        var ownerId = await factory.SeedUserAsync(Unique("tg-sold"), Password);
        var title = Title("продано <b>&</b>");
        var listingId = await factory.SeedListingAsync(ownerId, ListingStatus.Active, title: title);
        await factory.SetTelegramPostAsync(listingId, "@test_channel", 555);

        await factory.EnqueueOutboxAsync(
            OutboxMessage.ListingChannelUpdate, JsonSerializer.Serialize(new { listingId, mark = ChannelMark.Sold }));
        await factory.RunOutboxAsync();

        var edit = Assert.Single(factory.Bot.Edits, e => e.MessageId == 555);
        Assert.Equal("@test_channel", edit.ChatId);
        Assert.Equal($"{ChannelPostFormatter.EscapeHtml(title)}\n\n✅ ПРОДАНО", edit.Caption);
        Assert.Null(edit.Button);
    }

    [Fact]
    public async Task Channel_update_without_post_is_delivered_without_editing()
    {
        var ownerId = await factory.SeedUserAsync(Unique("tg-nopost"), Password);
        var listingId = await factory.SeedListingAsync(ownerId, ListingStatus.Active, title: Title("без поста"));
        var editsBefore = factory.Bot.Edits.Count;

        var id = await factory.EnqueueOutboxAsync(
            OutboxMessage.ListingChannelUpdate, JsonSerializer.Serialize(new { listingId, mark = ChannelMark.Archived }));
        await factory.RunOutboxAsync();

        Assert.Equal(OutboxStatus.Done, (await factory.OutboxStateAsync(id)).Status);
        Assert.Equal(editsBefore, factory.Bot.Edits.Count);
    }

    [Fact]
    public async Task Reannouncing_posted_listing_restores_caption_instead_of_reposting()
    {
        await factory.ClearChannelQueueAsync();
        var listingId = await SeedPublishableListingAsync("реактивация");
        await factory.SetTelegramPostAsync(listingId, "@test_channel", 777);

        await factory.EnqueueChannelPostAsync(listingId);
        await factory.RunOutboxAsync();

        Assert.Null(await factory.ChannelQueueItemAsync(listingId)); // второго поста не будет
        var edit = Assert.Single(factory.Bot.Edits, e => e.MessageId == 777);
        Assert.DoesNotContain("ПРОДАНО", edit.Caption);
        Assert.Equal($"https://api.test/r/l/{listingId}?s=tg", edit.Button?.Url);
    }

    private async Task<Guid> SeedPublishableListingAsync(string tag)
    {
        var ownerId = await factory.SeedUserAsync(Unique($"tg-{Guid.NewGuid():N}"), Password);
        var listingId = await factory.SeedListingAsync(ownerId, ListingStatus.Active, title: Title(tag));
        await factory.SeedListingImageAsync(listingId);
        return listingId;
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
