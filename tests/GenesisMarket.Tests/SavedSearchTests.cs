using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Сохранённые поиски: CRUD и лимит, привязка курсора при сохранении (без рассылки по
/// всему каталогу), курсорная идемпотентность рассылки, часовой гейт, недоверие к jsonb.
/// БД общая в рамках класса — тесты изолируются уникальным FTS-маркером в заголовке.
/// </summary>
public class SavedSearchTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    /// <summary>
    /// Ключевой инвариант из ТЗ: два прогона подряд без новых объявлений дают ровно
    /// одно уведомление. Первый прогон находит новое объявление и ставит одно сообщение
    /// в outbox; второй — уже ничего (курсор сдвинут, часовой гейт закрыт).
    /// </summary>
    [Fact]
    public async Task Two_runs_without_new_listings_yield_exactly_one_notification()
    {
        var (client, subscriber) = await NewSubscriber("ss-one");
        var owner = await factory.SeedUserAsync(Unique("ss-owner"), Password);
        var marker = Marker();

        // Поиск создаётся, когда подходящих объявлений ещё нет → курсор «пуст».
        var searchId = await CreateSearchAsync(client, marker);

        // Уже ПОСЛЕ сохранения появляется подходящее объявление.
        await factory.SeedListingAsync(owner, title: $"Диван {marker} угловой", category: Category.Home);

        var first = await factory.RunSavedSearchNotificationsAsync();
        Assert.Equal(1, first.Notified);

        var second = await factory.RunSavedSearchNotificationsAsync();
        Assert.Equal(0, second.Notified);

        // Ровно одно сообщение в outbox по этому поиску.
        Assert.Equal(1, await factory.OutboxCountAsync(OutboxMessage.SavedSearchMatch, searchId));
    }

    /// <summary>
    /// Идемпотентность именно по курсору, а не по времени: даже когда часовой гейт открыт
    /// (NotifiedAt сдвинут в прошлое), уже уведомлённое объявление повторно не рассылается.
    /// </summary>
    [Fact]
    public async Task Cursor_prevents_renotification_even_after_hour_window()
    {
        var (client, _) = await NewSubscriber("ss-cursor");
        var owner = await factory.SeedUserAsync(Unique("ss-cursor-owner"), Password);
        var marker = Marker();

        var searchId = await CreateSearchAsync(client, marker);
        await factory.SeedListingAsync(owner, title: $"Кресло {marker} мягкое", category: Category.Home);

        Assert.Equal(1, (await factory.RunSavedSearchNotificationsAsync()).Notified);

        // Открываем часовой гейт — но новых объявлений нет, курсор уже за этим объявлением.
        await factory.SetSavedSearchNotifiedAtAsync(searchId, DateTimeOffset.UtcNow.AddHours(-2));
        Assert.Equal(0, (await factory.RunSavedSearchNotificationsAsync()).Notified);
    }

    /// <summary>
    /// Новое объявление в течение часа после письма не рассылается (не чаще раза в час),
    /// но не теряется: как только час прошёл — уходит следующим уведомлением.
    /// </summary>
    [Fact]
    public async Task New_listing_within_hour_is_deferred_then_delivered_after_window()
    {
        var (client, _) = await NewSubscriber("ss-hour");
        var owner = await factory.SeedUserAsync(Unique("ss-hour-owner"), Password);
        var marker = Marker();

        var searchId = await CreateSearchAsync(client, marker);
        await factory.SeedListingAsync(owner, title: $"Стол {marker} обеденный", category: Category.Home);
        Assert.Equal(1, (await factory.RunSavedSearchNotificationsAsync()).Notified);

        // Второе подходящее объявление — но час ещё не прошёл.
        await factory.SeedListingAsync(owner, title: $"Шкаф {marker} книжный", category: Category.Home);
        Assert.Equal(0, (await factory.RunSavedSearchNotificationsAsync()).Notified);

        // Час прошёл — второе объявление доезжает.
        await factory.SetSavedSearchNotifiedAtAsync(searchId, DateTimeOffset.UtcNow.AddHours(-2));
        Assert.Equal(1, (await factory.RunSavedSearchNotificationsAsync()).Notified);

        // Всего два письма по поиску: без потерь и без дублей.
        Assert.Equal(2, await factory.OutboxCountAsync(OutboxMessage.SavedSearchMatch, searchId));
    }

    /// <summary>
    /// Привязка курсора при сохранении: объявления, существовавшие ДО создания поиска,
    /// не рассылаются (иначе новый поиск завалил бы подписчика всем каталогом).
    /// </summary>
    [Fact]
    public async Task Listings_existing_before_save_are_not_notified()
    {
        var (client, _) = await NewSubscriber("ss-anchor");
        var owner = await factory.SeedUserAsync(Unique("ss-anchor-owner"), Password);
        var marker = Marker();

        // Объявление уже есть ДО создания поиска.
        await factory.SeedListingAsync(owner, title: $"Диван {marker} старый", category: Category.Home);
        var searchId = await CreateSearchAsync(client, marker);

        Assert.Equal(0, (await factory.RunSavedSearchNotificationsAsync()).Notified);
        Assert.Equal(0, await factory.OutboxCountAsync(OutboxMessage.SavedSearchMatch, searchId));

        // Курсор привязан к самому свежему совпадению на момент сохранения.
        Assert.NotNull((await factory.SavedSearchStateAsync(searchId)).LastNotifiedListingId);
    }

    /// <summary>Джоб не доверяет jsonb: поиск с некорректными критериями деактивируется, без рассылки.</summary>
    [Fact]
    public async Task Invalid_stored_query_deactivates_search_without_notifying()
    {
        var (client, _) = await NewSubscriber("ss-bad");
        var marker = Marker();
        var searchId = await CreateSearchAsync(client, marker);

        // Валидный JSON, но недопустимые фильтры (priceFrom > priceTo) — как «порча» в хранилище.
        await factory.SetSavedSearchQueryJsonAsync(searchId, "{\"priceFrom\": 100, \"priceTo\": 10}");

        var result = await factory.RunSavedSearchNotificationsAsync();
        Assert.Equal(0, result.Notified);
        Assert.False((await factory.SavedSearchStateAsync(searchId)).IsActive);
    }

    /// <summary>Уведомление доставляется диспетчером outbox и закрывается как Done.</summary>
    [Fact]
    public async Task Dispatcher_delivers_saved_search_notification()
    {
        var (client, _) = await NewSubscriber("ss-deliver");
        var owner = await factory.SeedUserAsync(Unique("ss-deliver-owner"), Password);
        var marker = Marker();

        var searchId = await CreateSearchAsync(client, marker);
        await factory.SeedListingAsync(owner, title: $"Диван {marker} новый", category: Category.Home);
        Assert.Equal(1, (await factory.RunSavedSearchNotificationsAsync()).Notified);

        var result = await factory.RunOutboxAsync();
        Assert.True(result.Delivered >= 1);
    }

    // ---- CRUD и лимиты ----

    [Fact]
    public async Task Crud_roundtrip_lists_updates_and_deletes()
    {
        var (client, _) = await NewSubscriber("ss-crud");
        var marker = Marker();
        var id = await CreateSearchAsync(client, marker);

        // GET списка содержит созданный поиск.
        var list = await client.GetFromJsonAsync<JsonElement>("/api/saved-searches");
        Assert.Contains(list.EnumerateArray(), e => e.GetProperty("id").GetGuid() == id);

        // PATCH: имя и канал.
        var patch = await client.PatchAsJsonAsync($"/api/saved-searches/{id}",
            new { name = "Переименованный", notifyChannel = "None" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var patched = await patch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Переименованный", patched.GetProperty("name").GetString());
        Assert.Equal("None", patched.GetProperty("notifyChannel").GetString());

        // DELETE.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/saved-searches/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/saved-searches/{id}")).StatusCode);
    }

    [Fact]
    public async Task Active_limit_is_enforced_at_ten()
    {
        var (client, _) = await NewSubscriber("ss-limit");

        for (var i = 0; i < 10; i++)
        {
            var ok = await client.PostAsJsonAsync("/api/saved-searches",
                new { name = $"Поиск {i}", query = new { q = Marker() }, notifyChannel = "Email" });
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        var eleventh = await client.PostAsJsonAsync("/api/saved-searches",
            new { name = "Лишний", query = new { q = Marker() }, notifyChannel = "Email" });
        Assert.Equal(HttpStatusCode.Conflict, eleventh.StatusCode);
    }

    [Fact]
    public async Task Too_many_cities_is_rejected_on_create()
    {
        var (client, _) = await NewSubscriber("ss-cities");
        var allEight = new[] { "tiraspol", "bendery", "rybnitsa", "dubossary", "slobodzea", "grigoriopol", "dnestrovsk", "tiraspol" };

        var resp = await client.PostAsJsonAsync("/api/saved-searches",
            new { name = "Все города", query = new { cities = allEight }, notifyChannel = "Email" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Anonymous_cannot_manage_saved_searches()
    {
        var anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/saved-searches")).StatusCode);
    }

    // ---- helpers ----

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";

    /// <summary>Уникальный латинский FTS-маркер — изолирует объявления теста в общей БД.</summary>
    private static string Marker() => "zxqmark" + Guid.NewGuid().ToString("N")[..10];

    private async Task<(HttpClient Client, Guid UserId)> NewSubscriber(string prefix)
    {
        var email = Unique(prefix);
        var userId = await factory.SeedUserAsync(email, Password);
        return (await AuthedClient(email), userId);
    }

    private static async Task<Guid> CreateSearchAsync(HttpClient client, string marker)
    {
        var resp = await client.PostAsJsonAsync("/api/saved-searches",
            new { name = "Диваны", query = new { q = marker }, notifyChannel = "Email" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<HttpClient> AuthedClient(string email)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }
}
