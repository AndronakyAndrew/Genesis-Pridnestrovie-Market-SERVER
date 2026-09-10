using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Кеширование выдачи картинок. Ключ объекта неизменяем (новая картинка — новый GUIDv7),
/// поэтому URL фото объявления помечается immutable, а повтор обслуживается 304-м.
/// </summary>
public class ImageCachingTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    [Fact]
    public async Task Listing_image_is_served_with_immutable_cache_control_and_etag()
    {
        var (client, url) = await UploadAndGetUrl();

        var resp = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("public, max-age=31536000, immutable", resp.Headers.CacheControl?.ToString());
        Assert.NotNull(resp.Headers.ETag);
        Assert.False(resp.Headers.ETag!.IsWeak);
    }

    [Fact]
    public async Task Repeat_request_with_matching_if_none_match_returns_304_without_body()
    {
        var (client, url) = await UploadAndGetUrl();

        var first = await client.GetAsync(url);
        var etag = first.Headers.ETag!;
        Assert.True((await first.Content.ReadAsByteArrayAsync()).Length > 0);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, url);
        conditional.Headers.IfNoneMatch.Add(etag);
        var second = await client.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
        // 304 обязан нести валидатор и директивы кеширования (RFC 9110 §15.4.5).
        Assert.Equal(etag, second.Headers.ETag);
        Assert.Equal("public, max-age=31536000, immutable", second.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Stale_if_none_match_returns_body_again()
    {
        var (client, url) = await UploadAndGetUrl();

        using var conditional = new HttpRequestMessage(HttpMethod.Get, url);
        conditional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"0badc0de0badc0de\""));
        var resp = await client.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True((await resp.Content.ReadAsByteArrayAsync()).Length > 0);
    }

    [Fact]
    public async Task Thumb_and_original_have_different_etags()
    {
        var (client, url) = await UploadAndGetUrl();
        var (_, thumbUrl) = (client, await ThumbUrlFor(client, url));

        var original = await client.GetAsync(url);
        var thumb = await client.GetAsync(thumbUrl);

        Assert.NotEqual(original.Headers.ETag, thumb.Headers.ETag);
    }

    [Fact]
    public async Task Missing_image_is_404_and_is_not_cached()
    {
        var client = factory.CreateClient();

        var resp = await client.GetAsync(
            $"/api/images/listings/{Guid.NewGuid()}/{Guid.NewGuid()}.webp");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        // Годовой Cache-Control на 404 «прибил» бы URL в кеше клиента навсегда.
        Assert.Null(resp.Headers.CacheControl);
    }

    // ---- helpers ----

    private async Task<(HttpClient Client, string Url)> UploadAndGetUrl()
    {
        var email = Unique("cache");
        var ownerId = await factory.SeedUserAsync(email, Password);
        var listingId = await factory.SeedListingAsync(ownerId);
        var client = await AuthedClient(email);

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(SmallJpeg());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(file, "file", "photo.jpg");

        var resp = await client.PostAsync($"/api/listings/{listingId}/images", content);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var dto = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return (client, dto.GetProperty("url").GetString()!);
    }

    private static async Task<string> ThumbUrlFor(HttpClient client, string url)
    {
        // Из /api/images/listings/{listingId}/{guid}.webp достаём список и берём thumbUrl.
        var listingId = new Uri(url).Segments[^2].TrimEnd('/');
        var images = await client.GetFromJsonAsync<JsonElement>($"/api/listings/{listingId}/images");
        return images[0].GetProperty("thumbUrl").GetString()!;
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

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

    private static byte[] SmallJpeg()
    {
        using var image = new Image<Rgba32>(200, 150);
        image.Mutate(x => x.BackgroundColor(Color.SlateGray));
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms);
        return ms.ToArray();
    }
}
