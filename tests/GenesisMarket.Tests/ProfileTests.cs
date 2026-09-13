using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace GenesisMarket.Tests;

public class ProfileTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    [Fact]
    public async Task Patch_with_role_admin_in_body_does_not_change_role()
    {
        var email = Unique("patchrole");
        await factory.SeedUserAsync(email, Password); // роль User
        var client = await AuthedClient(email);

        // В тело кладём легитимное поле + запрещённые (role/email/isBanned).
        var patch = await client.PatchAsJsonAsync("/api/me", new
        {
            displayName = "Новое имя",
            role = "Admin",
            isBanned = false,
            email = "hacker@evil.io"
        });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal("User", me.GetProperty("role").GetString());          // роль не изменилась
        Assert.Equal("Новое имя", me.GetProperty("displayName").GetString()); // легитимное поле применилось
        Assert.Equal(email, me.GetProperty("email").GetString());          // email не изменился
    }

    [Fact]
    public async Task Public_profile_never_contains_email_phone_or_role()
    {
        var email = Unique("pub");
        var userId = await factory.SeedUserAsync(email, Password);
        var client = factory.CreateClient();

        var resp = await client.GetAsync($"/api/users/{userId}/public");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain(email, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"email\"", body, StringComparison.OrdinalIgnoreCase);
        // Номер телефона не раскрывается (флаг phoneVerified — можно, он в спеке).
        Assert.DoesNotContain("phoneE164", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("+373", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"role\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isbanned", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_me_excludes_password_hash_and_security_stamp()
    {
        var email = Unique("me");
        await factory.SeedUserAsync(email, Password);
        var client = await AuthedClient(email);

        var resp = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordhash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("securitystamp", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_me_requires_authentication()
    {
        var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_me_anonymizes_archives_listings_and_invalidates_token()
    {
        var email = Unique("del");
        var userId = await factory.SeedUserAsync(email, Password);
        var listingId = await factory.SeedListingAsync(userId);
        var client = await AuthedClient(email);

        var delete = await client.DeleteAsync("/api/me");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        // Токен удалённого пользователя перестаёт работать сразу.
        var afterMe = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, afterMe.StatusCode);

        // Публичный профиль удалённого — 404.
        var pub = await factory.CreateClient().GetAsync($"/api/users/{userId}/public");
        Assert.Equal(HttpStatusCode.NotFound, pub.StatusCode);

        // Анонимизация и архивация — проверяем в БД.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
        Assert.True(user.IsDeleted);
        Assert.Equal($"deleted-{userId}@invalid", user.Email);
        Assert.Null(user.PhoneE164);

        var status = await db.Listings.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.Id == listingId).Select(l => l.Status).FirstAsync();
        Assert.Equal(ListingStatus.Archived, status);
    }

    [Fact]
    public async Task Patch_changing_phone_resets_verification()
    {
        var email = Unique("phone");
        await factory.SeedUserAsync(email, Password, phoneVerified: true);
        var client = await AuthedClient(email);

        var patch = await client.PatchAsJsonAsync("/api/me", new { phoneE164 = "+37377654321" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal("+37377654321", me.GetProperty("phoneE164").GetString());
        Assert.False(me.GetProperty("phoneVerified").GetBoolean()); // сброшено сменой номера
    }

    [Fact]
    public async Task Avatar_endpoints_return_a_public_api_url_not_an_internal_storage_key()
    {
        var email = Unique("avatar");
        var userId = await factory.SeedUserAsync(email, Password);
        var client = await AuthedClient(email);

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(JpegWithGps()), "file", "avatar.jpg");
        var upload = await client.PostAsync("/api/me/avatar", form);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        // Адрес аватара строится по «ID профиля»: Guid — это UUID v7, то есть
        // время регистрации, и в публичной ссылке ему не место.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var code = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId).Select(u => u.PublicCode).FirstAsync();
        var expected = $"/api/users/by-code/{code}/avatar?v=";

        var uploadedUrl = (await upload.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("avatarUrl").GetString();
        Assert.Contains(expected, uploadedUrl);
        Assert.DoesNotContain(userId.ToString(), uploadedUrl);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Contains(expected, me.GetProperty("avatarUrl").GetString());

        var publicProfile = await factory.CreateClient()
            .GetFromJsonAsync<JsonElement>($"/api/users/by-code/{code}/public");
        Assert.Contains(expected, publicProfile.GetProperty("avatarUrl").GetString());

        var avatar = await factory.CreateClient().GetAsync($"/api/users/by-code/{code}/avatar");
        Assert.Equal(HttpStatusCode.OK, avatar.StatusCode);
        // Аватар нормализуется в WebP тем же конвейером, что и фото объявлений.
        Assert.Equal("image/webp", avatar.Content.Headers.ContentType?.MediaType);

        // Legacy-адрес по Guid продолжает работать — по нему уже расшарены ссылки.
        var legacy = await factory.CreateClient().GetAsync($"/api/users/{userId}/avatar");
        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
    }

    /// <summary>
    /// Регрессия: аватар — публичный анонимный эндпоинт, а в EXIF снимка лежат
    /// GPS-координаты, то есть домашний адрес продавца. Загрузка обязана снимать
    /// метаданные ДО записи в хранилище (раньше байты клали как пришли).
    /// </summary>
    [Fact]
    public async Task Uploaded_avatar_with_gps_has_no_exif_after_processing()
    {
        var email = Unique("avatar-exif");
        var userId = await factory.SeedUserAsync(email, Password);
        var client = await AuthedClient(email);

        var jpeg = JpegWithGps();
        // Исходник действительно содержит GPS — иначе тест проверял бы пустоту.
        using (var src = Image.Load(jpeg))
            Assert.NotNull(src.Metadata.ExifProfile);

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(jpeg), "file", "avatar.jpg");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/me/avatar", form)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = await db.Profiles.AsNoTracking()
            .Where(p => p.UserId == userId).Select(p => p.AvatarUrl!).FirstAsync();

        Assert.True(factory.Storage.TryGet(key, out var stored));
        Assert.Equal("WEBP", Image.DetectFormat(stored).Name, ignoreCase: true);

        using var processed = Image.Load(stored);
        Assert.Null(processed.Metadata.ExifProfile);
        Assert.Null(processed.Metadata.IptcProfile);
        Assert.Null(processed.Metadata.XmpProfile);
    }

    /// <summary>
    /// Удаление аватара: загрузка → удаление → повторное удаление. Второй DELETE
    /// обязан отдать 204, а не 404: кнопка «Удалить» на фронтенде может отправить
    /// запрос дважды, и это не ошибка. Объект из хранилища уходит заявкой в outbox
    /// (как у фото объявлений), поэтому перед проверкой прогоняем диспетчер.
    /// </summary>
    [Fact]
    public async Task Avatar_delete_is_idempotent_and_removes_the_object_from_storage()
    {
        var email = Unique("avatar-delete");
        var userId = await factory.SeedUserAsync(email, Password);
        var client = await AuthedClient(email);

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(JpegWithGps()), "file", "avatar.jpg");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/me/avatar", form)).StatusCode);

        var key = await AvatarKeyAsync(userId);
        Assert.NotNull(key);
        Assert.True(factory.Storage.Exists(key));

        // Первое удаление.
        var first = await client.DeleteAsync("/api/me/avatar");
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        // Ссылка снята сразу, в том же запросе.
        Assert.Null(await AvatarKeyAsync(userId));
        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal(JsonValueKind.Null, me.GetProperty("avatarUrl").ValueKind);

        // Публичный профиль тоже без аватара, а сама выдача файла — 404.
        var code = me.GetProperty("publicCode").GetString();
        var publicProfile = await factory.CreateClient()
            .GetFromJsonAsync<JsonElement>($"/api/users/by-code/{code}/public");
        Assert.Equal(JsonValueKind.Null, publicProfile.GetProperty("avatarUrl").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient()
            .GetAsync($"/api/users/by-code/{code}/avatar")).StatusCode);

        // Повторное удаление — тоже 204, без 404.
        var second = await client.DeleteAsync("/api/me/avatar");
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        // После доставки outbox объекта в хранилище больше нет.
        await factory.RunOutboxAsync();
        Assert.False(factory.Storage.Exists(key));
    }

    /// <summary>
    /// Замена аватара не оставляет сироту в хранилище: прежний объект уходит
    /// на удаление тем же путём. Раньше каждая повторная загрузка добавляла
    /// в MinIO файл, на который уже никто не ссылается.
    /// </summary>
    [Fact]
    public async Task Replacing_an_avatar_removes_the_previous_object()
    {
        var email = Unique("avatar-replace");
        var userId = await factory.SeedUserAsync(email, Password);
        var client = await AuthedClient(email);

        using var first = new MultipartFormDataContent();
        first.Add(new ByteArrayContent(JpegWithGps()), "file", "avatar.jpg");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/me/avatar", first)).StatusCode);
        var oldKey = await AvatarKeyAsync(userId);

        using var second = new MultipartFormDataContent();
        second.Add(new ByteArrayContent(JpegWithGps()), "file", "avatar.jpg");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/me/avatar", second)).StatusCode);
        var newKey = await AvatarKeyAsync(userId);

        Assert.NotNull(oldKey);
        Assert.NotNull(newKey);
        Assert.NotEqual(oldKey, newKey);

        await factory.RunOutboxAsync();
        Assert.False(factory.Storage.Exists(oldKey));
        Assert.True(factory.Storage.Exists(newKey));   // текущий на месте
    }

    /// <summary>Удаление аккаунта уносит и файл аватара, а не только ссылку на него.</summary>
    [Fact]
    public async Task Deleting_the_account_removes_the_avatar_object()
    {
        var email = Unique("avatar-account-delete");
        var userId = await factory.SeedUserAsync(email, Password);
        var client = await AuthedClient(email);

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(JpegWithGps()), "file", "avatar.jpg");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/me/avatar", form)).StatusCode);

        var key = await AvatarKeyAsync(userId);
        Assert.NotNull(key);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/me")).StatusCode);

        await factory.RunOutboxAsync();
        Assert.False(factory.Storage.Exists(key));
    }

    /// <summary>Не-изображение с «правильным» именем и Content-Type в аватары не проходит.</summary>
    [Fact]
    public async Task Avatar_upload_rejects_content_that_is_not_an_image()
    {
        var email = Unique("avatar-fake");
        await factory.SeedUserAsync(email, Password);
        var client = await AuthedClient(email);

        using var form = new MultipartFormDataContent();
        var payload = new ByteArrayContent("<?php system($_GET['c']); ?>"u8.ToArray());
        payload.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(payload, "file", "avatar.jpg");

        var resp = await client.PostAsync("/api/me/avatar", form);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    /// <summary>
    /// «О себе»: сохраняется нормализованным (пробелы по краям сняты, CRLF → LF,
    /// 4 переноса подряд → 2), возвращается владельцу в /api/me и любому
    /// постороннему в публичном профиле. Раньше поле молча терялось: фронт слал
    /// его в PATCH, сервер не биндил, текст жил только в состоянии React.
    /// </summary>
    [Fact]
    public async Task Description_is_saved_normalized_and_visible_to_owner_and_to_strangers()
    {
        var email = Unique("bio");
        var userId = await factory.SeedUserAsync(email, Password);
        var client = await AuthedClient(email);

        var patch = await client.PatchAsJsonAsync("/api/me", new
        {
            description = "  Чиню телефоны с 2015 года.\r\n\r\n\r\n\r\nПишите в Telegram.  "
        });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        const string expected = "Чиню телефоны с 2015 года.\n\nПишите в Telegram.";

        // Ответ на сам PATCH — уже нормализованный текст, без второго запроса.
        Assert.Equal(expected, (await patch.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("description").GetString());

        // Владелец: своё описание в приватном профиле.
        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal(expected, me.GetProperty("description").GetString());

        // Чужой залогиненный пользователь: через публичный профиль.
        var strangerEmail = Unique("bio-stranger");
        await factory.SeedUserAsync(strangerEmail, Password);
        var stranger = await AuthedClient(strangerEmail);
        var seenByStranger = await stranger.GetFromJsonAsync<JsonElement>($"/api/users/{userId}/public");
        Assert.Equal(expected, seenByStranger.GetProperty("description").GetString());

        // И анонимный посетитель — описание публично по замыслу.
        var seenByAnonymous = await factory.CreateClient()
            .GetFromJsonAsync<JsonElement>($"/api/users/{userId}/public");
        Assert.Equal(expected, seenByAnonymous.GetProperty("description").GetString());

        // В БД лежит ровно нормализованный текст, а не то, что пришло по проводу.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Profiles.AsNoTracking()
            .Where(p => p.UserId == userId).Select(p => p.Description).FirstAsync();
        Assert.Equal(expected, stored);
    }

    /// <summary>
    /// Очистка описания: пустая строка (или строка из одних пробелов и переносов)
    /// после нормализации ложится в БД как NULL, а не как "" — иначе «пусто»
    /// имело бы два разных представления.
    /// </summary>
    [Fact]
    public async Task Blank_description_is_stored_as_null_not_as_empty_string()
    {
        var email = Unique("bio-blank");
        var userId = await factory.SeedUserAsync(email, Password);
        var client = await AuthedClient(email);

        Assert.Equal(HttpStatusCode.OK,
            (await client.PatchAsJsonAsync("/api/me", new { description = "Было что рассказать" })).StatusCode);

        var cleared = await client.PatchAsJsonAsync("/api/me", new { description = "   \r\n  \t " });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal(JsonValueKind.Null, me.GetProperty("description").ValueKind);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Profiles.AsNoTracking()
            .Where(p => p.UserId == userId).Select(p => p.Description).FirstAsync();
        Assert.Null(stored);
    }

    /// <summary>Граница длины: 300 символов проходят, 301 — 400, в БД ничего не меняется.</summary>
    [Fact]
    public async Task Description_longer_than_300_characters_is_rejected()
    {
        var email = Unique("bio-long");
        var userId = await factory.SeedUserAsync(email, Password);
        var client = await AuthedClient(email);

        var maxLength = new string('я', 300);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PatchAsJsonAsync("/api/me", new { description = maxLength })).StatusCode);

        var tooLong = await client.PatchAsJsonAsync("/api/me", new { description = new string('я', 301) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        // Отказ не задел уже сохранённое значение.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Profiles.AsNoTracking()
            .Where(p => p.UserId == userId).Select(p => p.Description).FirstAsync();
        Assert.Equal(maxLength, stored);
    }

    // ---- helpers ----

    /// <summary>Настоящий JPEG с GPS-координатами в EXIF — вход для проверок обработки.</summary>
    private static byte[] JpegWithGps()
    {
        using var image = new Image<Rgba32>(600, 600);
        image.Mutate(x => x.BackgroundColor(Color.CornflowerBlue));

        var exif = new ExifProfile();
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(46), new Rational(50), new Rational(0)]);
        exif.SetValue(ExifTag.GPSLongitudeRef, "E");
        exif.SetValue(ExifTag.GPSLongitude, [new Rational(29), new Rational(38), new Rational(0)]);
        exif.SetValue(ExifTag.Make, "GenesisTestCam");
        image.Metadata.ExifProfile = exif;

        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms);
        return ms.ToArray();
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";

    /// <summary>Ключ объекта аватара в хранилище (в БД лежит именно он, не URL).</summary>
    private async Task<string?> AvatarKeyAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Profiles.AsNoTracking()
            .Where(p => p.UserId == userId).Select(p => p.AvatarUrl).FirstAsync();
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
