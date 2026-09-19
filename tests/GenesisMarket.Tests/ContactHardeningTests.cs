using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Scheduling;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Закрытие путей, которыми контакты продавца утекали мимо
/// <c>GET /api/listings/{id}/contact</c> и мимо его лимита:
/// неподтверждённая почта, квота в памяти процесса, чужие черновики,
/// телефон в тексте объявления и бессрочное хранение IpHash.
/// </summary>
public class ContactHardeningTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";
    private const string SellerPhone = "+37312345678";

    // ---- Обязательное подтверждение почты ----

    [Fact]
    public async Task Unverified_email_cannot_reveal_contact()
    {
        var ownerId = await factory.SeedUserAsync(Unique("hard-owner"), Password);
        await factory.ConfigureContactAsync(ownerId, showPhone: true);
        var listingId = await factory.SeedListingAsync(ownerId);

        var viewer = Unique("hard-unverified");
        await factory.SeedUserAsync(viewer, Password, emailVerified: false);
        var client = await AuthedClient(viewer);

        var resp = await client.GetAsync($"/api/listings/{listingId}/contact");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        // Телефон не просочился ни в каком виде, включая текст ошибки.
        Assert.DoesNotContain("37312345678", await resp.Content.ReadAsStringAsync());
        // Раскрытие не записано: отказ не должен открывать гейт отзывов.
        Assert.Equal(0, await factory.ContactRevealCountAsync(listingId));
    }

    [Fact]
    public async Task Verified_email_reveals_contact()
    {
        var ownerId = await factory.SeedUserAsync(Unique("hard-owner2"), Password);
        await factory.ConfigureContactAsync(ownerId, showPhone: true);
        var listingId = await factory.SeedListingAsync(ownerId);

        var viewer = Unique("hard-verified");
        await factory.SeedUserAsync(viewer, Password, emailVerified: true);
        var client = await AuthedClient(viewer);

        var resp = await client.GetAsync($"/api/listings/{listingId}/contact");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(SellerPhone, body.GetProperty("phone").GetString());
    }

    [Fact]
    public async Task Registration_sends_email_code()
    {
        var email = Unique("hard-register");
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = Password,
            displayName = "Новый",
            city = "Tiraspol",
            phone = "+37377712345"
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Код ушёл сразу при регистрации — отдельного нажатия «отправить код» не нужно.
        Assert.NotNull(factory.LastCode(email));
    }

    // ---- Квота раскрытий живёт в БД, а не в памяти процесса ----

    [Fact]
    public async Task Quota_counts_journal_so_it_survives_restart()
    {
        var ownerId = await factory.SeedUserAsync(Unique("hard-quota-owner"), Password);
        await factory.ConfigureContactAsync(ownerId, showPhone: true);
        var listingId = await factory.SeedListingAsync(ownerId);

        var viewer = Unique("hard-quota");
        var viewerId = await factory.SeedUserAsync(viewer, Password, emailVerified: true);

        // Засеваем квоту прямо в журнал: счётчик встроенного RateLimiter при этом
        // пуст — ровно как после рестарта контейнера. Прежняя реализация пустила бы.
        await factory.SeedContactRevealsAsync(listingId, viewerId, count: 30);

        var client = await AuthedClient(viewer);
        var resp = await client.GetAsync($"/api/listings/{listingId}/contact");

        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
        Assert.NotNull(resp.Headers.RetryAfter);
    }

    [Fact]
    public async Task Quota_window_is_rolling_so_old_reveals_do_not_count()
    {
        var ownerId = await factory.SeedUserAsync(Unique("hard-window-owner"), Password);
        await factory.ConfigureContactAsync(ownerId, showPhone: true);
        var listingId = await factory.SeedListingAsync(ownerId);

        var viewer = Unique("hard-window");
        var viewerId = await factory.SeedUserAsync(viewer, Password, emailVerified: true);

        // Те же 30 раскрытий, но двухчасовой давности — за окном.
        await factory.SeedContactRevealsAsync(
            listingId, viewerId, count: 30, age: TimeSpan.FromHours(2));

        var client = await AuthedClient(viewer);
        var resp = await client.GetAsync($"/api/listings/{listingId}/contact");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ---- Неопубликованное — не для посторонних ----

    [Theory]
    [InlineData(ListingStatus.Draft)]
    [InlineData(ListingStatus.PendingReview)]
    [InlineData(ListingStatus.Rejected)]
    public async Task Unpublished_listing_is_hidden_from_stranger(ListingStatus status)
    {
        var ownerId = await factory.SeedUserAsync(Unique($"hard-unpub-{status}"), Password);
        var listingId = await factory.SeedListingAsync(
            ownerId, status: status, description: "Продам, звоните +373 777 12 345");

        var stranger = factory.CreateClient();
        var byId = await stranger.GetAsync($"/api/listings/{listingId}");

        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);
        Assert.DoesNotContain("77712345", await byId.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unpublished_listing_is_visible_to_owner()
    {
        var email = Unique("hard-owner-sees");
        var ownerId = await factory.SeedUserAsync(email, Password);
        var listingId = await factory.SeedListingAsync(ownerId, status: ListingStatus.Draft);

        var client = await AuthedClient(email);
        var resp = await client.GetAsync($"/api/listings/{listingId}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Unpublished_listing_images_are_hidden_from_stranger()
    {
        // Фотографии должны следовать видимости карточки: иначе закрытая карточка
        // закрыта только на словах.
        var ownerId = await factory.SeedUserAsync(Unique("hard-img"), Password);
        var listingId = await factory.SeedListingAsync(ownerId, status: ListingStatus.Draft);

        var stranger = factory.CreateClient();
        var resp = await stranger.GetAsync($"/api/listings/{listingId}/images");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Published_listing_images_stay_public()
    {
        var ownerId = await factory.SeedUserAsync(Unique("hard-img-pub"), Password);
        var listingId = await factory.SeedListingAsync(ownerId);

        var stranger = factory.CreateClient();
        var resp = await stranger.GetAsync($"/api/listings/{listingId}/images");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Sold_listing_stays_reachable_by_direct_link()
    {
        // Sold и Archived намеренно остаются доступными: на них ссылаются из переписки
        // и поисковиков. Проверка от чрезмерной блокировки.
        var ownerId = await factory.SeedUserAsync(Unique("hard-sold"), Password);
        var listingId = await factory.SeedListingAsync(ownerId, status: ListingStatus.Sold);

        var stranger = factory.CreateClient();
        var resp = await stranger.GetAsync($"/api/listings/{listingId}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ---- Контакты в тексте объявления ----

    [Fact]
    public async Task Description_contacts_are_redacted_for_stranger_and_raw_for_owner()
    {
        var email = Unique("hard-text");
        var ownerId = await factory.SeedUserAsync(email, Password);
        var listingId = await factory.SeedListingAsync(ownerId, description:
            "Диван в хорошем состоянии. Тел +373 777 12 345, пишите @ivan_sell или на ivan@mail.ru");

        // Постороннему — без контактов.
        var stranger = factory.CreateClient();
        var raw = await stranger.GetStringAsync($"/api/listings/{listingId}");
        foreach (var leak in new[] { "+373", "77712345", "@ivan_sell", "ivan@mail.ru" })
            Assert.DoesNotContain(leak, raw);
        // Сам текст при этом остался читаемым.
        Assert.Contains("Диван в хорошем состоянии", raw);

        // Владельцу — как написал.
        var owner = await AuthedClient(email);
        var own = await owner.GetStringAsync($"/api/listings/{listingId}");
        Assert.Contains("@ivan_sell", own);
    }

    [Fact]
    public async Task Seo_meta_redacts_contacts_from_description()
    {
        var ownerId = await factory.SeedUserAsync(Unique("hard-seo"), Password);
        var listingId = await factory.SeedListingAsync(ownerId, description:
            "Срочно. Звоните +373 777 12 345 или t.me/ivan_sell");

        var client = factory.CreateClient();
        var meta = await client.GetStringAsync($"/api/listings/{listingId}/meta");

        // Мета уходит в поисковый индекс — это самая публичная копия описания.
        foreach (var leak in new[] { "+373", "77712345", "t.me/ivan_sell" })
            Assert.DoesNotContain(leak, meta);
    }

    // ---- Срок хранения IpHash ----

    [Fact]
    public async Task Retention_strips_iphash_but_keeps_reveal_row()
    {
        var ownerId = await factory.SeedUserAsync(Unique("hard-retention"), Password);
        var listingId = await factory.SeedListingAsync(ownerId);
        var viewerId = await factory.SeedUserAsync(Unique("hard-retention-viewer"), Password);

        await factory.SeedContactRevealsAsync(
            listingId, viewerId, count: 1, age: TimeSpan.FromDays(120), ipHash: "OLDHASH");
        await factory.SeedContactRevealsAsync(
            listingId, viewerId, count: 1, age: TimeSpan.FromDays(10), ipHash: "FRESHHASH");

        await factory.RunJournalRetentionAsync();

        var hashes = await factory.ContactRevealHashesAsync(listingId);

        // Строки на месте: на них держатся contactRevealCount и гейт отзывов.
        Assert.Equal(2, hashes.Count);
        Assert.Equal(2, await factory.ContactRevealCountAsync(listingId));
        // Старый IpHash снят, свежий не тронут.
        Assert.Contains(JournalRetentionService.ExpiredIpHash, hashes);
        Assert.Contains("FRESHHASH", hashes);
        Assert.DoesNotContain("OLDHASH", hashes);
    }

    [Fact]
    public async Task Retention_deletes_old_link_clicks()
    {
        var ownerId = await factory.SeedUserAsync(Unique("hard-clicks"), Password);
        var listingId = await factory.SeedListingAsync(ownerId);

        await factory.SeedLinkClickAsync(listingId, age: TimeSpan.FromDays(120));
        await factory.SeedLinkClickAsync(listingId, age: TimeSpan.FromDays(10));

        await factory.RunJournalRetentionAsync();

        // Переходы никем не агрегируются и растут быстрее всех — удаляются целиком.
        Assert.Equal(1, await factory.LinkClickCountAsync(listingId));
    }

    // ---- helpers ----

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";

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
