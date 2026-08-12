using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Публичная прокси-отдача картинок через API (единый домен, MinIO приватный).
/// Проверяем: байты отдаются, отсутствующий/чужой ключ → 404, а в каталоге и списке
/// изображений URL ведут на /api/images, а не на presigned MinIO.
/// </summary>
public class ImageProxyTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Password = "CorrectHorse7";

    [Fact]
    public async Task Proxy_serves_bytes_and_404s_for_missing_or_foreign_keys()
    {
        var ownerId = await factory.SeedUserAsync(Unique("img-owner"), Password);
        var listingId = await factory.SeedListingAsync(ownerId);

        var key = $"listings/{listingId}/{Guid.NewGuid():N}.webp";
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        await using (var ms = new MemoryStream(bytes))
            await factory.Storage.PutAsync(key, ms, ms.Length, "image/webp");

        var client = factory.CreateClient(); // аноним — картинки публичны

        var ok = await client.GetAsync($"/api/images/{key}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("image/webp", ok.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await ok.Content.ReadAsByteArrayAsync());

        // Отсутствующий объект.
        var missing = await client.GetAsync($"/api/images/listings/{listingId}/{Guid.NewGuid():N}.webp");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // Ключ вне префикса listings/ не обслуживается.
        var foreign = await client.GetAsync("/api/images/secrets/config.json");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task Catalog_and_images_endpoint_return_proxy_urls()
    {
        var ownerId = await factory.SeedUserAsync(Unique("url-owner"), Password);
        var listingId = await factory.SeedListingAsync(ownerId);
        await factory.SeedListingImageAsync(listingId);

        var client = factory.CreateClient();

        // Список изображений объявления.
        var images = await client.GetFromJsonAsync<JsonElement>($"/api/listings/{listingId}/images");
        var first = images.EnumerateArray().First();
        var url = first.GetProperty("url").GetString();
        var thumb = first.GetProperty("thumbUrl").GetString();
        Assert.Contains($"/api/images/listings/{listingId}/", url);
        Assert.StartsWith("http", url);
        Assert.Contains("/api/images/listings/", thumb);

        // Карточка каталога (у этого приложения своя БД — в каталоге только наше объявление).
        var page = await client.GetFromJsonAsync<JsonElement>("/api/listings");
        var item = page.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("id").GetGuid() == listingId);
        var firstImageUrl = item.GetProperty("firstImageUrl").GetString();
        Assert.Contains($"/api/images/listings/{listingId}/", firstImageUrl);
        Assert.StartsWith("http", firstImageUrl);
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.io";
}
