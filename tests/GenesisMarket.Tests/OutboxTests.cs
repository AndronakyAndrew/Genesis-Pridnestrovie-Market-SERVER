using System.Text.Json;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Транзакционный outbox: атомарность записи сообщения с доменным изменением,
/// доставка диспетчером, ретраи с экспоненциальной задержкой и финализация в Failed,
/// вычистка персональных данных из поля Error.
/// </summary>
public class OutboxTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    /// <summary>
    /// Ключевой инвариант: сообщение outbox живёт в той же транзакции, что и доменное
    /// изменение. Если транзакция создания объявления откатывается — сообщения в outbox нет.
    /// </summary>
    [Fact]
    public async Task Rollback_of_listing_creation_leaves_no_outbox_message()
    {
        var ownerId = await factory.SeedUserAsync(Unique("outbox-rollback"), Password);
        var marker = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync();

            var listing = new Listing
            {
                Slug = $"outbox-{marker:N}",
                Title = "Объявление для теста отката",
                Description = "Тестовое описание объявления для проверки отката транзакции",
                Price = 1000,
                PriceType = PriceType.Fixed,
                Category = Category.Other,
                SubcategoryId = 42,
                City = City.Tiraspol,
                Condition = Condition.Used,
                Status = ListingStatus.Draft,
                OwnerId = ownerId
            };
            db.Listings.Add(listing);

            // Уведомление — в той же транзакции. Marker кладём в payload, чтобы найти его потом.
            db.OutboxMessages.Add(new OutboxMessage
            {
                Type = OutboxMessage.ListingApproved,
                Payload = JsonSerializer.Serialize(new { listingId = listing.Id, marker })
            });

            await db.SaveChangesAsync();

            // Транзакция откатывается (эмуляция сбоя после записи в outbox).
            await tx.RollbackAsync();
        }

        // Ни объявления, ни сообщения не осталось.
        Assert.Equal(0, await factory.OutboxCountAsync(OutboxMessage.ListingApproved, marker));

        using var check = factory.Services.CreateScope();
        var db2 = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db2.Listings.IgnoreQueryFilters()
            .AnyAsync(l => l.Slug == $"outbox-{marker:N}"));
    }

    /// <summary>Готовое сообщение диспетчер доставляет и закрывает как Done.</summary>
    [Fact]
    public async Task Dispatcher_delivers_notification_and_marks_done()
    {
        var ownerId = await factory.SeedUserAsync(Unique("outbox-deliver"), Password);
        var listingId = await factory.SeedListingAsync(ownerId, ListingStatus.Active);

        var id = await factory.EnqueueOutboxAsync(
            OutboxMessage.ListingApproved,
            JsonSerializer.Serialize(new { listingId }));

        var result = await factory.RunOutboxAsync();

        Assert.True(result.Delivered >= 1);
        var state = await factory.OutboxStateAsync(id);
        Assert.Equal(OutboxStatus.Done, state.Status);
        Assert.Equal(1, state.Attempts);
        Assert.Null(state.Error);
    }

    /// <summary>
    /// Транзиентный сбой: сообщение остаётся Pending, счётчик попыток растёт, повтор
    /// откладывается (NextAttemptAt в будущем). После MaxAttempts — терминальный Failed.
    /// Персональные данные из текста ошибки вычищены.
    /// </summary>
    [Fact]
    public async Task Transient_failures_retry_then_escalate_to_failed_without_pii()
    {
        var id = await factory.EnqueueOutboxAsync(TestFailingOutboxHandler.FailType, "{}");

        // Первая попытка.
        var first = await factory.RunOutboxAsync();
        Assert.Equal(1, first.Retrying);

        var afterFirst = await factory.OutboxStateAsync(id);
        Assert.Equal(OutboxStatus.Pending, afterFirst.Status);
        Assert.Equal(1, afterFirst.Attempts);
        Assert.True(afterFirst.NextAttemptAt > DateTimeOffset.UtcNow); // повтор отложен

        // PII вычищены: ни адреса, ни телефона; вместо них плейсхолдеры.
        Assert.NotNull(afterFirst.Error);
        Assert.DoesNotContain("user@example.com", afterFirst.Error);
        Assert.DoesNotContain("+37312345678", afterFirst.Error);
        Assert.Contains("[email]", afterFirst.Error);
        Assert.Contains("[phone]", afterFirst.Error);

        // Догоняем оставшиеся попытки, каждый раз делая сообщение «готовым».
        for (var i = 0; i < OutboxMessage.MaxAttempts; i++)
        {
            await factory.MakeOutboxDueAsync(id);
            await factory.RunOutboxAsync();
        }

        var final = await factory.OutboxStateAsync(id);
        Assert.Equal(OutboxStatus.Failed, final.Status);
        Assert.Equal(OutboxMessage.MaxAttempts, final.Attempts);
    }

    /// <summary>Некорректное сообщение (адресат/ресурс отсутствует) — сразу Failed, без ретраев.</summary>
    [Fact]
    public async Task Permanent_failure_goes_straight_to_failed()
    {
        // Объявления с таким id нет — обработчик бросит OutboxPermanentException.
        var id = await factory.EnqueueOutboxAsync(
            OutboxMessage.ListingApproved,
            JsonSerializer.Serialize(new { listingId = Guid.NewGuid() }));

        var result = await factory.RunOutboxAsync();

        Assert.Equal(1, result.Failed);
        var state = await factory.OutboxStateAsync(id);
        Assert.Equal(OutboxStatus.Failed, state.Status);
        Assert.Equal(1, state.Attempts); // без повторов
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";
}
