using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Invitations;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Приглашения продавцов: права на создание, жизненный цикл ссылки (одноразовость, срок,
/// отзыв, перевыпуск), гонка принятия, изоляция чужих приглашений и черновиков, согласие
/// на публикацию, невидимость черновиков в публичной выдаче, регистрация по ссылке и очистка.
///
/// Каждый клиент получает свой IP (<see cref="Client"/>): ручки по токену под лимитом на IP,
/// и без этого тесты класса делили бы один счётчик.
/// </summary>
public class SellerInvitationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";
    private const string ConsentVersion = "2026-10-01";

    // ---- Права ----

    [Theory]
    [InlineData(UserRole.User)]
    [InlineData(UserRole.Moderator)]
    public async Task Role_without_permission_cannot_create_invitation(UserRole role)
    {
        var (client, _) = await Staff(role);
        var resp = await client.PostAsJsonAsync("/api/admin/invitations", Shop());
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Link_cannot_be_created_without_drafts()
    {
        var (admin, _) = await Staff(UserRole.Admin);
        var id = await CreateInvitation(admin);

        var resp = await admin.PostAsync($"/api/admin/invitations/{id}/link", null);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        await using var scope = Db(out var db);
        var inv = await db.SellerInvitations.SingleAsync(i => i.Id == id);
        Assert.Equal(InvitationStatus.Draft, inv.Status);
        Assert.Null(inv.TokenHash);
    }

    [Fact]
    public async Task Admin_sees_only_own_invitations_owner_sees_all()
    {
        var (adminA, _) = await Staff(UserRole.Admin);
        var (adminB, _) = await Staff(UserRole.Admin);
        var id = await CreateInvitation(adminA);
        await AddDraft(adminA, id);

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/admin/invitations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await adminB.PostAsJsonAsync($"/api/admin/invitations/{id}/drafts", Draft("Чужой товар для проверки"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsync($"/api/admin/invitations/{id}/revoke", null)).StatusCode);
        var list = await adminB.GetFromJsonAsync<JsonElement>("/api/admin/invitations");
        Assert.DoesNotContain(list.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == id);

        var (owner, _) = await Staff(UserRole.Owner);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/admin/invitations/{id}")).StatusCode);
        // Владелец площадки может выпускать и отзывать ссылки чужих приглашений.
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/admin/invitations/{id}/link", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/admin/invitations/{id}/reissue", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/admin/invitations/{id}/revoke", null)).StatusCode);
    }

    // ---- Жизненный цикл ссылки ----

    [Fact]
    public async Task Accept_moves_drafts_to_owner_and_link_is_single_use()
    {
        var (admin, adminId) = await Staff(UserRole.Admin);
        var id = await CreateInvitation(admin);
        var draftId = await AddDraft(admin, id);
        await UploadPhoto(admin, id, draftId);
        var token = await IssueLink(admin, id);

        var anon = Client();
        var preview = await anon.GetAsync($"/api/invitations/{token}");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var body = await preview.Content.ReadAsStringAsync();
        Assert.DoesNotContain("internalNote", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Внутренняя заметка", body);

        var (owner, ownerId) = await User();
        var accept = await owner.PostAsync($"/api/invitations/{token}/accept", null);
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);

        await using (var scope = Db(out var db))
        {
            var inv = await db.SellerInvitations.SingleAsync(i => i.Id == id);
            Assert.Equal(InvitationStatus.Accepted, inv.Status);
            Assert.Equal(ownerId, inv.AcceptedByUserId);
            Assert.Null(inv.TokenHash);

            var listing = await db.Listings.SingleAsync(l => l.SourceInvitationId == id);
            Assert.Equal(ownerId, listing.OwnerId);
            Assert.Equal(ListingStatus.Draft, listing.Status);
            Assert.Equal(adminId, listing.PreparedByUserId);
            Assert.Single(await db.ListingImages.Where(i => i.ListingId == listing.Id).ToListAsync());

            Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "invitation.accept" && e.TargetId == id.ToString()
                                                           && e.ActorId == ownerId));
            Assert.True(await db.OutboxMessages.AnyAsync(m => m.Type == OutboxMessage.InvitationAccepted));
        }

        // Повторное использование: ни предпросмотра, ни второго принятия.
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/invitations/{token}")).StatusCode);
        var (other, _) = await User();
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/invitations/{token}/accept", null)).StatusCode);

        // Фото переехали под listings/ после доставки outbox.
        await factory.RunOutboxAsync();
        await using (var scope = Db(out var db))
        {
            var image = await db.ListingImages.SingleAsync(i => i.Listing!.SourceInvitationId == id);
            Assert.True(factory.Storage.Exists(image.ObjectKey));
            Assert.DoesNotContain(factory.Storage.Keys, k => k.StartsWith($"invitations/{id}/"));
        }

        // Правка принятого приглашения запрещена.
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PostAsJsonAsync($"/api/admin/invitations/{id}/drafts", Draft("Товар после принятия"))).StatusCode);
    }

    [Fact]
    public async Task Expired_link_is_gone()
    {
        var (admin, _) = await Staff(UserRole.Admin);
        var id = await CreateInvitation(admin);
        await AddDraft(admin, id);
        var token = await IssueLink(admin, id);

        await factory.ExecuteSqlAsync(
            $"UPDATE seller_invitations SET \"ExpiresAt\" = now() - interval '1 minute' WHERE \"Id\" = '{id}'");

        Assert.Equal(HttpStatusCode.Gone, (await Client().GetAsync($"/api/invitations/{token}")).StatusCode);
        var (owner, _) = await User();
        Assert.Equal(HttpStatusCode.Gone, (await owner.PostAsync($"/api/invitations/{token}/accept", null)).StatusCode);

        // Истёкшую можно перевыпустить.
        var reissue = await admin.PostAsync($"/api/admin/invitations/{id}/reissue", null);
        Assert.Equal(HttpStatusCode.OK, reissue.StatusCode);
    }

    [Fact]
    public async Task Revoked_link_does_not_work()
    {
        var (admin, _) = await Staff(UserRole.Admin);
        var id = await CreateInvitation(admin);
        await AddDraft(admin, id);
        var token = await IssueLink(admin, id);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/invitations/{id}/revoke", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync($"/api/invitations/{token}")).StatusCode);
        var (owner, _) = await User();
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/invitations/{token}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/admin/invitations/{id}/reissue", null)).StatusCode);

        await using var scope = Db(out var db);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "invitation.revoke" && e.TargetId == id.ToString()));
    }

    [Fact]
    public async Task Reissue_invalidates_old_link()
    {
        var (admin, _) = await Staff(UserRole.Admin);
        var id = await CreateInvitation(admin);
        await AddDraft(admin, id);
        var oldToken = await IssueLink(admin, id);

        var reissue = await admin.PostAsync($"/api/admin/invitations/{id}/reissue", null);
        Assert.Equal(HttpStatusCode.OK, reissue.StatusCode);
        var newToken = TokenOf(await reissue.Content.ReadFromJsonAsync<JsonElement>());
        Assert.NotEqual(oldToken, newToken);

        var anon = Client();
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/invitations/{oldToken}")).StatusCode);
        var (owner, _) = await User();
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/invitations/{oldToken}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/invitations/{newToken}/accept", null)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_accepts_produce_exactly_one_owner()
    {
        var (admin, _) = await Staff(UserRole.Admin);
        var id = await CreateInvitation(admin);
        await AddDraft(admin, id);
        await AddDraft(admin, id, "Второй товар магазина для гонки");
        var token = await IssueLink(admin, id);

        var owners = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => User()));
        var responses = await Task.WhenAll(owners.Select(o => o.Client.PostAsync($"/api/invitations/{token}/accept", null)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK),
            r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.NotFound }));

        await using var scope = Db(out var db);
        var listings = await db.Listings.Where(l => l.SourceInvitationId == id).ToListAsync();
        Assert.Equal(2, listings.Count);
        Assert.Single(listings.Select(l => l.OwnerId).Distinct());
    }

    [Fact]
    public async Task Creator_cannot_accept_own_invitation()
    {
        var (admin, _) = await Staff(UserRole.Admin);
        var id = await CreateInvitation(admin);
        await AddDraft(admin, id);
        var token = await IssueLink(admin, id);

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync($"/api/invitations/{token}/accept", null)).StatusCode);
    }

    [Fact]
    public async Task Token_endpoint_is_rate_limited_per_ip()
    {
        var anon = Client();
        var fake = new string('A', 43);
        HttpStatusCode last = default;
        for (var i = 0; i < 31; i++)
            last = (await anon.GetAsync($"/api/invitations/{fake}")).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, last);

        // Другой адрес — свой счётчик.
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync($"/api/invitations/{fake}")).StatusCode);
    }

    // ---- Кабинет владельца: согласие и чужие черновики ----

    [Fact]
    public async Task Publish_requires_consent_and_records_it()
    {
        var (listingId, owner, _) = await AcceptedDraft();

        var noConsent = await owner.PostAsJsonAsync("/api/me/invited-drafts/publish",
            new { listingIds = new[] { listingId }, consentAccepted = false, consentVersion = ConsentVersion });
        Assert.Equal(HttpStatusCode.BadRequest, noConsent.StatusCode);

        var staleVersion = await owner.PostAsJsonAsync("/api/me/invited-drafts/publish",
            new { listingIds = new[] { listingId }, consentAccepted = true, consentVersion = "2000-01-01" });
        Assert.Equal(HttpStatusCode.Conflict, staleVersion.StatusCode);

        // Обычная ручка публикации не обходит согласие.
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"/api/listings/{listingId}/publish", null)).StatusCode);

        await using (var scope = Db(out var db))
        {
            Assert.Equal(ListingStatus.Draft, (await db.Listings.SingleAsync(l => l.Id == listingId)).Status);
            Assert.False(await db.PublicationConsents.AnyAsync(c => c.ListingId == listingId));
        }

        var ok = await owner.PostAsJsonAsync("/api/me/invited-drafts/publish",
            new { listingIds = new[] { listingId }, consentAccepted = true, consentVersion = ConsentVersion });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var result = (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results")[0];
        Assert.Equal(JsonValueKind.Null, result.GetProperty("error").ValueKind);

        await using (var scope = Db(out var db))
        {
            var listing = await db.Listings.SingleAsync(l => l.Id == listingId);
            Assert.NotEqual(ListingStatus.Draft, listing.Status);
            var consent = await db.PublicationConsents.SingleAsync(c => c.ListingId == listingId);
            Assert.Equal((listing.OwnerId, ConsentVersion), (consent.UserId, consent.Version));
            Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "invitation.publish" && e.TargetId == listingId.ToString()));
        }
    }

    [Fact]
    public async Task Other_user_cannot_see_or_publish_invited_drafts()
    {
        var (listingId, _, _) = await AcceptedDraft();
        var (stranger, _) = await User();

        var mine = await stranger.GetFromJsonAsync<JsonElement>("/api/me/invited-drafts");
        Assert.Empty(mine.GetProperty("items").EnumerateArray());

        var publish = await stranger.PostAsJsonAsync("/api/me/invited-drafts/publish",
            new { listingIds = new[] { listingId }, consentAccepted = true, consentVersion = ConsentVersion });
        var result = (await publish.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results")[0];
        Assert.Equal("Товар не найден", result.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/listings/{listingId}/publish", null)).StatusCode);

        await using var scope = Db(out var db);
        Assert.Equal(ListingStatus.Draft, (await db.Listings.SingleAsync(l => l.Id == listingId)).Status);
    }

    // ---- Публичная выдача ----

    [Fact]
    public async Task Drafts_are_absent_from_public_endpoints()
    {
        var (admin, _) = await Staff(UserRole.Admin);
        var id = await CreateInvitation(admin);
        var marker = $"Приглашение {Guid.NewGuid():N}"[..40];
        var draftId = await AddDraft(admin, id, marker);
        await UploadPhoto(admin, id, draftId);
        await IssueLink(admin, id);

        var anon = Client();
        var search = await anon.GetFromJsonAsync<JsonElement>($"/api/listings?q={Uri.EscapeDataString(marker)}");
        Assert.Empty(search.GetProperty("items").EnumerateArray());
        var catalog = await anon.GetStringAsync("/api/listings?limit=50");
        Assert.DoesNotContain(marker, catalog);

        // Фото черновика не отдаётся ни публичной выдачей, ни ручкой фото без подписи.
        await using (var scope = Db(out var db))
        {
            var photo = await db.InvitationDraftPhotos.SingleAsync(p => p.DraftId == draftId);
            Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/invitation-photos/{photo.Id}?v=o&exp=9999999999&sig=x")).StatusCode);
            var file = photo.ObjectKey[(photo.ObjectKey.LastIndexOf('/') + 1)..];
            Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/images/listings/{id}/{file}")).StatusCode);
        }

        // Подписанная ссылка из мастера работает.
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/invitations/{id}");
        var url = new Uri(detail.GetProperty("drafts")[0].GetProperty("photos")[0].GetProperty("thumbUrl").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync(url.PathAndQuery)).StatusCode);

        // robots.txt закрывает страницу приглашения.
        Assert.Contains("/invite/", string.Join(' ', GenesisMarket.Api.Seo.SeoUrls.DisallowedPaths));
    }

    [Fact]
    public async Task Accepted_draft_listing_is_not_public()
    {
        var (listingId, _, _) = await AcceptedDraft();
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync($"/api/listings/{listingId}")).StatusCode);
    }

    // ---- Регистрация по ссылке ----

    [Fact]
    public async Task Signup_creates_passwordless_account_named_after_shop()
    {
        var (admin, _) = await Staff(UserRole.Admin);
        var id = await CreateInvitation(admin, "Лавка у рынка");
        await AddDraft(admin, id);
        var token = await IssueLink(admin, id);

        var email = Unique("shop");
        var anon = Client();
        var signup = await anon.PostAsJsonAsync($"/api/invitations/{token}/signup", new { email });
        Assert.Equal(HttpStatusCode.OK, signup.StatusCode);

        // Уже существующий адрес — тот же ответ (без оракула «есть ли аккаунт»).
        var existing = Unique("existing");
        await factory.SeedUserAsync(existing, Password);
        var again = await anon.PostAsJsonAsync($"/api/invitations/{token}/signup", new { email = existing });
        Assert.Equal(await signup.Content.ReadAsStringAsync(), await again.Content.ReadAsStringAsync());

        await using (var scope = Db(out var db))
        {
            var user = await db.Users.Include(u => u.Profile).SingleAsync(u => u.Email == email);
            Assert.False(user.HasPassword);
            Assert.Equal("Лавка у рынка", user.Profile!.DisplayName);
            Assert.Equal(City.Tiraspol, user.Profile.City);
        }

        var code = factory.LastCode(email);
        Assert.NotNull(code);
        var login = await anon.PostAsJsonAsync("/api/auth/login-code/verify", new { email, code });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var access = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        anon.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);

        Assert.Equal(HttpStatusCode.OK, (await anon.PostAsync($"/api/invitations/{token}/accept", null)).StatusCode);

        var drafts = await anon.GetFromJsonAsync<JsonElement>("/api/me/invited-drafts");
        Assert.Single(drafts.GetProperty("items").EnumerateArray());
        Assert.False(drafts.GetProperty("hasPassword").GetBoolean());
        Assert.Equal(ConsentVersion, drafts.GetProperty("consentVersion").GetString());

        // Предложение задать пароль — одноразовое.
        Assert.Equal(HttpStatusCode.NoContent,
            (await anon.PostAsJsonAsync("/api/me/password", new { newPassword = "ShopOwner2026!" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await anon.PostAsJsonAsync("/api/me/password", new { newPassword = "Another2026!x" })).StatusCode);
        var pwLogin = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = "ShopOwner2026!" });
        Assert.Equal(HttpStatusCode.OK, pwLogin.StatusCode);
    }

    // ---- Письмо о принятии ----

    [Theory]
    [InlineData(1, "черновик")]
    [InlineData(3, "черновика")]
    [InlineData(5, "черновиков")]
    [InlineData(11, "черновиков")]
    [InlineData(21, "черновик")]
    [InlineData(24, "черновика")]
    public void Acceptance_email_declines_drafts_count(int n, string expected) =>
        Assert.Equal(expected, GenesisMarket.Api.Outbox.InvitationAcceptedHandler.Drafts(n));

    // ---- Очистка ----

    [Fact]
    public async Task Cleanup_expires_and_purges_but_keeps_accepted()
    {
        var (admin, _) = await Staff(UserRole.Admin);

        var pending = await CreateInvitation(admin);
        await AddDraft(admin, pending);
        await IssueLink(admin, pending);
        await factory.ExecuteSqlAsync(
            $"UPDATE seller_invitations SET \"ExpiresAt\" = now() - interval '1 hour' WHERE \"Id\" = '{pending}'");

        var oldRevoked = await CreateInvitation(admin);
        var revokedDraft = await AddDraft(admin, oldRevoked);
        await UploadPhoto(admin, oldRevoked, revokedDraft);
        await admin.PostAsync($"/api/admin/invitations/{oldRevoked}/revoke", null);
        await factory.ExecuteSqlAsync(
            $"UPDATE seller_invitations SET \"StatusChangedAt\" = now() - interval '31 days' WHERE \"Id\" = '{oldRevoked}'");

        var abandoned = await CreateInvitation(admin);
        await factory.ExecuteSqlAsync(
            $"UPDATE seller_invitations SET \"CreatedAt\" = now() - interval '61 days', \"UpdatedAt\" = NULL WHERE \"Id\" = '{abandoned}'");

        var freshDraft = await CreateInvitation(admin);

        var (acceptedListing, _, accepted) = await AcceptedDraft();
        await factory.ExecuteSqlAsync(
            $"UPDATE seller_invitations SET \"StatusChangedAt\" = now() - interval '400 days' WHERE \"Id\" = '{accepted}'");

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IInvitationCleanupService>().RunAsync(default);

        await using (var scope = Db(out var db))
        {
            Assert.Equal(InvitationStatus.Expired, (await db.SellerInvitations.SingleAsync(i => i.Id == pending)).Status);
            Assert.False(await db.SellerInvitations.AnyAsync(i => i.Id == oldRevoked));
            Assert.False(await db.InvitationDrafts.AnyAsync(d => d.Id == revokedDraft));
            Assert.False(await db.SellerInvitations.AnyAsync(i => i.Id == abandoned));
            Assert.True(await db.SellerInvitations.AnyAsync(i => i.Id == freshDraft));
            Assert.True(await db.SellerInvitations.AnyAsync(i => i.Id == accepted));
            Assert.True(await db.Listings.AnyAsync(l => l.Id == acceptedListing));
            Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "invitation.purge" && e.TargetId == oldRevoked.ToString()));
        }

        await factory.RunOutboxAsync();
        Assert.DoesNotContain(factory.Storage.Keys, k => k.StartsWith($"invitations/{oldRevoked}/"));
    }

    // ---- helpers ----

    /// <summary>Принятое приглашение с одним товаром: (объявление, клиент владельца, приглашение).</summary>
    private async Task<(Guid ListingId, HttpClient Owner, Guid InvitationId)> AcceptedDraft()
    {
        var (admin, _) = await Staff(UserRole.Admin);
        var id = await CreateInvitation(admin);
        await AddDraft(admin, id, $"Товар магазина {Guid.NewGuid():N}"[..30]);
        var token = await IssueLink(admin, id);

        var (owner, _) = await User();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/invitations/{token}/accept", null)).StatusCode);

        await using var scope = Db(out var db);
        var listingId = await db.Listings.Where(l => l.SourceInvitationId == id).Select(l => l.Id).SingleAsync();
        return (listingId, owner, id);
    }

    private async Task<Guid> CreateInvitation(HttpClient admin, string shopName = "Магазин тестовый")
    {
        var resp = await admin.PostAsJsonAsync("/api/admin/invitations", Shop(shopName));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<Guid> AddDraft(HttpClient admin, Guid invitationId, string title = "Диван угловой из магазина")
    {
        var resp = await admin.PostAsJsonAsync($"/api/admin/invitations/{invitationId}/drafts", Draft(title));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task UploadPhoto(HttpClient admin, Guid invitationId, Guid draftId)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(SmallJpeg());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(file, "file", "photo.jpg");
        var resp = await admin.PostAsync($"/api/admin/invitations/{invitationId}/drafts/{draftId}/photos", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private async Task<string> IssueLink(HttpClient admin, Guid invitationId)
    {
        var resp = await admin.PostAsync($"/api/admin/invitations/{invitationId}/link", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return TokenOf(await resp.Content.ReadFromJsonAsync<JsonElement>());
    }

    private static string TokenOf(JsonElement link)
    {
        var url = link.GetProperty("url").GetString()!;
        Assert.StartsWith("https://market.test/invite/", url);
        return url[(url.LastIndexOf('/') + 1)..];
    }

    private static object Shop(string shopName = "Магазин тестовый") => new
    {
        shopName, city = "tiraspol", category = "home", internalNote = "Внутренняя заметка сотрудника"
    };

    private static object Draft(string title) => new
    {
        title,
        description = "Хорошее состояние, самовывоз из магазина в центре города",
        priceType = "Fixed",
        price = 1500,
        category = "Home",
        subcategoryId = 18,
        condition = "Used"
    };

    private static byte[] SmallJpeg()
    {
        using var image = new Image<Rgba32>(200, 150);
        image.Mutate(x => x.BackgroundColor(Color.SlateGray));
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms);
        return ms.ToArray();
    }

    private AsyncServiceScope Db(out AppDbContext db)
    {
        var scope = factory.Services.CreateAsyncScope();
        db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return scope;
    }

    /// <summary>Клиент со своим адресом: лимиты по IP у каждого клиента свои.</summary>
    private HttpClient Client()
    {
        var ip = new IPAddress([10, (byte)Random.Shared.Next(256), (byte)Random.Shared.Next(256), (byte)Random.Shared.Next(1, 255)]);
        var handler = factory.Server.CreateHandler(ctx => ctx.Connection.RemoteIpAddress = ip);
        return new HttpClient(handler) { BaseAddress = factory.Server.BaseAddress };
    }

    private async Task<(HttpClient Client, Guid Id)> Staff(UserRole role)
    {
        var email = Unique(role.ToString().ToLowerInvariant());
        var id = await factory.SeedUserAsync(email, Password, role: role);
        var client = Client();
        var login = await factory.LoginAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var access = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        return (client, id);
    }

    private Task<(HttpClient Client, Guid Id)> User() => Staff(UserRole.User);

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";
}
