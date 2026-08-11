using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Domain.Enums;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Отзывы — ядро слоя доверия. Проверяем гейт анти-накрутки (нужно предшествующее
/// раскрытие контактов), уникальность отзыва на пару, запрет отзыва самому себе,
/// денормализованный агрегат рейтинга и скрытие модератором.
/// </summary>
public class ReviewsTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    [Fact]
    public async Task Review_without_prior_contact_reveal_is_rejected()
    {
        var (sellerId, listingId) = await SeedSellerWithListing();

        var buyer = Unique("buyer");
        await factory.SeedUserAsync(buyer, Password);
        var client = await AuthedClient(buyer);

        // Контакты продавца по этому объявлению НЕ раскрывались — отзыв запрещён.
        var resp = await client.PostAsJsonAsync("/api/reviews",
            new { listingId, rating = 5, text = "Отличный продавец" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal((null, 0), await factory.UserRatingAsync(sellerId));
    }

    [Fact]
    public async Task Second_review_on_same_listing_is_rejected()
    {
        var (_, listingId) = await SeedSellerWithListing();

        var buyer = Unique("buyer");
        var buyerId = await factory.SeedUserAsync(buyer, Password);
        await factory.SeedContactRevealAsync(listingId, buyerId);
        var client = await AuthedClient(buyer);

        var first = await client.PostAsJsonAsync("/api/reviews",
            new { listingId, rating = 5, text = "Первый отзыв" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/reviews",
            new { listingId, rating = 1, text = "Второй отзыв по тому же объявлению" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Self_review_is_rejected()
    {
        var (sellerId, listingId) = await SeedSellerWithListing();

        // Даже если продавец раскрыл собственные контакты — отзыв самому себе запрещён.
        await factory.SeedContactRevealAsync(listingId, sellerId);
        var client = await AuthedClient(await SellerEmail(sellerId));

        var resp = await client.PostAsJsonAsync("/api/reviews",
            new { listingId, rating = 5, text = "Сам себя хвалю" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Review_updates_denormalized_seller_rating()
    {
        var (sellerId, listingId) = await SeedSellerWithListing();

        var buyer1 = Unique("buyer1");
        var buyer1Id = await factory.SeedUserAsync(buyer1, Password);
        await factory.SeedContactRevealAsync(listingId, buyer1Id);
        var c1 = await AuthedClient(buyer1);
        Assert.Equal(HttpStatusCode.Created, (await c1.PostAsJsonAsync("/api/reviews",
            new { listingId, rating = 4, text = "Хорошо, но с задержкой" })).StatusCode);

        Assert.Equal((4.0, 1), await factory.UserRatingAsync(sellerId));

        var buyer2 = Unique("buyer2");
        var buyer2Id = await factory.SeedUserAsync(buyer2, Password);
        await factory.SeedContactRevealAsync(listingId, buyer2Id);
        var c2 = await AuthedClient(buyer2);
        Assert.Equal(HttpStatusCode.Created, (await c2.PostAsJsonAsync("/api/reviews",
            new { listingId, rating = 2, text = "Так себе, товар не соответствует" })).StatusCode);

        // Среднее пересчитано триггером в той же транзакции: (4 + 2) / 2 = 3.
        Assert.Equal((3.0, 2), await factory.UserRatingAsync(sellerId));

        // Публичная выдача отдаёт оба отзыва, свежий сверху.
        var page = await (await c2.GetAsync($"/api/users/{sellerId}/reviews"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        Assert.False(page.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task Hidden_review_disappears_from_feed_and_aggregate()
    {
        var (sellerId, listingId) = await SeedSellerWithListing();

        var buyer = Unique("buyer");
        var buyerId = await factory.SeedUserAsync(buyer, Password);
        await factory.SeedContactRevealAsync(listingId, buyerId);
        var buyerClient = await AuthedClient(buyer);

        var created = await buyerClient.PostAsJsonAsync("/api/reviews",
            new { listingId, rating = 1, text = "Клеветнический отзыв" });
        var reviewId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();
        Assert.Equal((1.0, 1), await factory.UserRatingAsync(sellerId));

        // Модератор скрывает отзыв.
        var mod = Unique("mod");
        await factory.SeedUserAsync(mod, Password, role: UserRole.Moderator);
        var modClient = await AuthedClient(mod);
        var hide = await modClient.PostAsync($"/api/reviews/{reviewId}/hide", null);
        Assert.Equal(HttpStatusCode.NoContent, hide.StatusCode);

        // Скрытый отзыв ушёл из агрегата и из публичной выдачи.
        Assert.Equal((null, 0), await factory.UserRatingAsync(sellerId));
        var page = await (await buyerClient.GetAsync($"/api/users/{sellerId}/reviews"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, page.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Author_can_edit_within_window_others_cannot()
    {
        var (sellerId, listingId) = await SeedSellerWithListing();

        var buyer = Unique("buyer");
        var buyerId = await factory.SeedUserAsync(buyer, Password);
        await factory.SeedContactRevealAsync(listingId, buyerId);
        var buyerClient = await AuthedClient(buyer);

        var created = await buyerClient.PostAsJsonAsync("/api/reviews",
            new { listingId, rating = 5, text = "Сначала всё понравилось" });
        var reviewId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        // Автор правит в течение окна 24ч — оценка меняется, агрегат пересчитан.
        var edit = await buyerClient.PutAsJsonAsync($"/api/reviews/{reviewId}",
            new { rating = 3, text = "Передумал: средне" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal((3.0, 1), await factory.UserRatingAsync(sellerId));

        // Чужой пользователь редактировать не может.
        var stranger = Unique("stranger");
        await factory.SeedUserAsync(stranger, Password);
        var strangerClient = await AuthedClient(stranger);
        var forbidden = await strangerClient.PutAsJsonAsync($"/api/reviews/{reviewId}",
            new { rating = 1, text = "Порчу чужой отзыв" });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    // ---- helpers ----

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";

    private async Task<(Guid SellerId, Guid ListingId)> SeedSellerWithListing()
    {
        var email = Unique("seller");
        var sellerId = await factory.SeedUserAsync(email, Password);
        _sellerEmails[sellerId] = email;
        var listingId = await factory.SeedListingAsync(sellerId);
        return (sellerId, listingId);
    }

    // SeedUserAsync не возвращает email — запоминаем сгенерированный, чтобы залогиниться продавцом.
    private readonly Dictionary<Guid, string> _sellerEmails = new();

    private Task<string> SellerEmail(Guid sellerId) => Task.FromResult(_sellerEmails[sellerId]);

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
