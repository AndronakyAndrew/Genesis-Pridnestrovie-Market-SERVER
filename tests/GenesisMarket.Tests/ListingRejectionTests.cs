using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Domain.Enums;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Причина отклонения объявления: видна автору, скрыта от всех остальных,
/// исчезает при возврате объявления в оборот.
/// </summary>
public class ListingRejectionTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";
    private const string Comment = "Телефон в описании — уберите, он подставится из профиля";

    /// <summary>
    /// Автор видит код, комментарий и дату отказа — и в карточке, и в «моих
    /// объявлениях». Без этого фронтенд показывает общее «объявление отклонено»,
    /// и человек публикует то же самое заново.
    /// </summary>
    [Fact]
    public async Task Owner_sees_the_rejection_reason()
    {
        var sellerEmail = Unique("reject-owner");
        var sellerId = await factory.SeedUserAsync(sellerEmail, Password);
        var listingId = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview);

        await RejectAsync(listingId, RejectionReasonCode.ContactsInText, Comment);

        var seller = await AuthedClient(sellerEmail);

        var card = await seller.GetFromJsonAsync<JsonElement>($"/api/listings/{listingId}");
        Assert.Equal("Rejected", card.GetProperty("status").GetString());
        Assert.Equal("ContactsInText", card.GetProperty("rejectionReasonCode").GetString());
        Assert.Equal(Comment, card.GetProperty("rejectionComment").GetString());
        Assert.NotEqual(JsonValueKind.Null, card.GetProperty("rejectedAt").ValueKind);

        // И в списке своих объявлений — фронтенд рисует вкладку «Отклонённые» из него.
        var mine = await seller.GetFromJsonAsync<JsonElement>("/api/me/listings?status=Rejected");
        var row = mine.EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == listingId);
        Assert.Equal("ContactsInText", row.GetProperty("rejectionReasonCode").GetString());
        Assert.Equal(Comment, row.GetProperty("rejectionComment").GetString());
    }

    /// <summary>
    /// Посторонний не видит ни кода, ни комментария. Проверка не формальная:
    /// GET /api/listings/{id} отдаёт объявление любому и в любом статусе, то есть
    /// без явного условия комментарий модератора стал бы публичным.
    /// </summary>
    [Fact]
    public async Task Rejection_reason_is_hidden_from_everyone_but_the_owner()
    {
        var sellerId = await factory.SeedUserAsync(Unique("reject-hidden"), Password);
        var listingId = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview);

        await RejectAsync(listingId, RejectionReasonCode.ContactsInText, Comment);

        // Аноним.
        var anonymous = factory.CreateClient();
        var raw = await anonymous.GetStringAsync($"/api/listings/{listingId}");
        Assert.DoesNotContain(Comment, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("ContactsInText", raw, StringComparison.Ordinal);

        var card = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(JsonValueKind.Null, card.GetProperty("rejectionReasonCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, card.GetProperty("rejectionComment").ValueKind);
        Assert.Equal(JsonValueKind.Null, card.GetProperty("rejectedAt").ValueKind);

        // Другой авторизованный пользователь — тоже посторонний.
        var strangerEmail = Unique("reject-stranger");
        await factory.SeedUserAsync(strangerEmail, Password);
        var stranger = await AuthedClient(strangerEmail);

        var seenByStranger = await stranger.GetFromJsonAsync<JsonElement>($"/api/listings/{listingId}");
        Assert.Equal(JsonValueKind.Null, seenByStranger.GetProperty("rejectionReasonCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, seenByStranger.GetProperty("rejectionComment").ValueKind);
    }

    /// <summary>
    /// Комментарий обязателен только при коде Other: он единственный ничего
    /// не объясняет сам по себе.
    /// </summary>
    [Fact]
    public async Task Comment_is_required_only_for_the_Other_reason()
    {
        var sellerId = await factory.SeedUserAsync(Unique("reject-comment"), Password);
        var mod = await Moderator();

        // Other без комментария — 400, объявление остаётся на модерации.
        var withoutComment = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview);
        var bad = await mod.PostAsJsonAsync($"/api/moderation/listings/{withoutComment}/reject",
            new { reason = nameof(RejectionReasonCode.Other), comment = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("PendingReview", (await factory.ListingModerationAsync(withoutComment)).Status);

        // Пробелы вместо комментария не считаются комментарием.
        var blank = await mod.PostAsJsonAsync($"/api/moderation/listings/{withoutComment}/reject",
            new { reason = nameof(RejectionReasonCode.Other), comment = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        // Other с комментарием — проходит.
        var good = await mod.PostAsJsonAsync($"/api/moderation/listings/{withoutComment}/reject",
            new { reason = nameof(RejectionReasonCode.Other), comment = "Объявление не о товаре" });
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);

        // Любой другой код без комментария — тоже проходит.
        var typed = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview);
        var ok = await mod.PostAsJsonAsync($"/api/moderation/listings/{typed}/reject",
            new { reason = nameof(RejectionReasonCode.Duplicate), comment = (string?)null });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    /// <summary>
    /// Возврат объявления в оборот стирает отметку об отказе: иначе причина
    /// прошлого отказа висела бы на уже исправленной версии.
    /// Доступный сейчас путь из Rejected — снять с публикации (архив) и восстановить.
    /// </summary>
    [Fact]
    public async Task Returning_a_rejected_listing_to_review_clears_the_reason()
    {
        var sellerEmail = Unique("reject-clear");
        var sellerId = await factory.SeedUserAsync(sellerEmail, Password);
        var listingId = await factory.SeedListingAsync(sellerId, ListingStatus.PendingReview);

        await RejectAsync(listingId, RejectionReasonCode.BadPhotos, "Фото размытые");

        var seller = await AuthedClient(sellerEmail);
        var rejected = await seller.GetFromJsonAsync<JsonElement>($"/api/listings/{listingId}");
        Assert.Equal("BadPhotos", rejected.GetProperty("rejectionReasonCode").GetString());

        // В архив и обратно.
        Assert.Equal(HttpStatusCode.NoContent,
            (await seller.DeleteAsync($"/api/listings/{listingId}")).StatusCode);
        var restore = await seller.PostAsync($"/api/listings/{listingId}/restore", null);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);

        var after = await restore.Content.ReadFromJsonAsync<JsonElement>();
        // Недавний отказ ⇒ снова на премодерацию, но уже без старой причины.
        Assert.Equal("PendingReview", after.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("rejectionReasonCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("rejectionComment").ValueKind);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("rejectedAt").ValueKind);

        // И при повторном чтении тоже пусто — не только в ответе на restore.
        var reread = await seller.GetFromJsonAsync<JsonElement>($"/api/listings/{listingId}");
        Assert.Equal(JsonValueKind.Null, reread.GetProperty("rejectionReasonCode").ValueKind);
    }

    // ---- helpers ----

    private async Task RejectAsync(Guid listingId, RejectionReasonCode reason, string? comment)
    {
        var mod = await Moderator();
        var resp = await mod.PostAsJsonAsync($"/api/moderation/listings/{listingId}/reject",
            new { reason = reason.ToString(), comment });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private async Task<HttpClient> Moderator()
    {
        var email = Unique("reject-mod");
        await factory.SeedUserAsync(email, Password, role: UserRole.Moderator);
        return await AuthedClient(email);
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";

    private async Task<HttpClient> AuthedClient(string email)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", doc.RootElement.GetProperty("accessToken").GetString());
        return client;
    }
}
