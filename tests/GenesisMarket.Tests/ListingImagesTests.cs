using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GenesisMarket.Domain.Entities;
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

public class ListingImagesTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    [Fact]
    public async Task File_with_php_content_and_jpg_extension_is_rejected()
    {
        var email = Unique("owner");
        var ownerId = await factory.SeedUserAsync(email, Password);
        var listingId = await factory.SeedListingAsync(ownerId);
        var client = await AuthedClient(email);

        // Содержимое — PHP, но расширение .jpg и Content-Type image/jpeg (враньё).
        // Строку собираем из фрагментов, чтобы антивирус не принял исходник за веб-шелл;
        // для теста важно лишь, что это не изображение.
        var payload = Encoding.UTF8.GetBytes("<" + "?php echo 'genesis'; ?" + ">");
        var resp = await Upload(client, listingId, payload, "shell.jpg", "image/jpeg");

        // Тип определяется по содержимому → не изображение → отклонено.
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, resp.StatusCode);
    }

    [Fact]
    public async Task Uploaded_jpeg_with_gps_has_no_exif_after_processing()
    {
        var email = Unique("owner");
        var ownerId = await factory.SeedUserAsync(email, Password);
        var listingId = await factory.SeedListingAsync(ownerId);
        var client = await AuthedClient(email);

        var jpeg = JpegWithGps();
        // Убеждаемся, что исходник действительно содержит EXIF/GPS.
        using (var src = Image.Load(jpeg))
            Assert.NotNull(src.Metadata.ExifProfile);

        var resp = await Upload(client, listingId, jpeg, "photo.jpg", "image/jpeg");
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        // Читаем сохранённый (обработанный) объект из фейкового хранилища по ключу из БД.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var image = await db.ListingImages.AsNoTracking().FirstAsync(i => i.ListingId == listingId);

        Assert.True(factory.Storage.TryGet(image.ObjectKey, out var stored));

        using var processed = Image.Load(stored);
        Assert.Null(processed.Metadata.ExifProfile);                 // EXIF снят полностью
        Assert.Equal("WEBP", Image.DetectFormat(stored).Name, ignoreCase: true); // перекодировано в WebP
    }

    [Fact]
    public async Task Upload_to_someone_elses_listing_returns_404()
    {
        var ownerId = await factory.SeedUserAsync(Unique("owner"), Password);
        var listingId = await factory.SeedListingAsync(ownerId);

        var otherEmail = Unique("other");
        await factory.SeedUserAsync(otherEmail, Password);
        var client = await AuthedClient(otherEmail);

        var resp = await Upload(client, listingId, SmallJpeg(), "photo.jpg", "image/jpeg");

        // Владелец не совпал ⇒ 404 (существование чужого объявления не подтверждаем).
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Reorder_changes_sort_order()
    {
        var email = Unique("owner");
        var ownerId = await factory.SeedUserAsync(email, Password);
        var listingId = await factory.SeedListingAsync(ownerId);
        var client = await AuthedClient(email);

        var first = await UploadOk(client, listingId);
        var second = await UploadOk(client, listingId);

        // Меняем порядок: второй становится первым.
        var reorder = await client.PatchAsJsonAsync(
            $"/api/listings/{listingId}/images/order",
            new { imageIds = new[] { second, first } });
        Assert.Equal(HttpStatusCode.OK, reorder.StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/listings/{listingId}/images");
        var ids = list.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([second, first], ids);
    }

    [Fact]
    public async Task Delete_removes_row_and_enqueues_outbox_deletion()
    {
        var email = Unique("owner");
        var ownerId = await factory.SeedUserAsync(email, Password);
        var listingId = await factory.SeedListingAsync(ownerId);
        var client = await AuthedClient(email);

        var imageId = await UploadOk(client, listingId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var keys = await db.ListingImages.AsNoTracking()
            .Where(i => i.Id == imageId)
            .Select(i => new { i.ObjectKey, i.ThumbKey })
            .FirstAsync();

        var resp = await client.DeleteAsync($"/api/listings/{listingId}/images/{imageId}");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        Assert.False(await db.ListingImages.AnyAsync(i => i.Id == imageId));

        // Удаление объектов ушло в outbox, а не выполнено синхронно в обработчике:
        // одно сообщение DeleteImages с ключами оригинала и превью.
        var pending = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.Status == OutboxStatus.Pending && m.Type == OutboxMessage.DeleteImages)
            .Select(m => m.Payload)
            .ToListAsync();
        Assert.Contains(pending, p => p.Contains(keys.ObjectKey) && p.Contains(keys.ThumbKey));

        // А диспетчер, отработав, реально удаляет объекты из хранилища и закрывает сообщение.
        Assert.True(factory.Storage.Exists(keys.ObjectKey));
        var result = await factory.RunOutboxAsync();
        Assert.True(result.Delivered >= 1);
        Assert.False(factory.Storage.Exists(keys.ObjectKey));
        Assert.False(factory.Storage.Exists(keys.ThumbKey));
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

    private static async Task<HttpResponseMessage> Upload(
        HttpClient client, Guid listingId, byte[] bytes, string fileName, string contentType)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "file", fileName);
        return await client.PostAsync($"/api/listings/{listingId}/images", content);
    }

    /// <summary>Загружает валидное изображение и возвращает его Id.</summary>
    private static async Task<Guid> UploadOk(HttpClient client, Guid listingId)
    {
        var resp = await Upload(client, listingId, SmallJpeg(), "photo.jpg", "image/jpeg");
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return dto.GetProperty("id").GetGuid();
    }

    private static byte[] SmallJpeg()
    {
        using var image = new Image<Rgba32>(200, 150);
        image.Mutate(x => x.BackgroundColor(Color.SlateGray));
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms);
        return ms.ToArray();
    }

    private static byte[] JpegWithGps()
    {
        using var image = new Image<Rgba32>(1200, 800);
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
}
