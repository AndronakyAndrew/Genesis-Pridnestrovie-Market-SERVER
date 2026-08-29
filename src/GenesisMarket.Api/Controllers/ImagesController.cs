using System.Text.RegularExpressions;
using GenesisMarket.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Публичная выдача фото объявлений через API. MinIO в приватной Docker-сети,
/// его внутренний адрес (и presigned-ссылки на него) браузеру недоступны —
/// байты стримим отсюда, как аватар через <c>GET /api/users/{id}/avatar</c>.
/// </summary>
[Route("api/images/listings")]
public class ImagesController(IObjectStorage storage) : ApiControllerBase
{
    // listings/{listingId}/{guid}.webp или .../{guid}_thumb.webp — ключ генерирует сервер.
    private static readonly Regex SafeFileName = new(
        @"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(_thumb)?\.webp$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [AllowAnonymous]
    [HttpGet("{listingId:guid}/{file}")]
    public async Task<IActionResult> Get(Guid listingId, string file, CancellationToken ct)
    {
        if (!SafeFileName.IsMatch(file))
            return Problem(title: "Изображение не найдено", statusCode: StatusCodes.Status404NotFound);

        var objectKey = $"listings/{listingId}/{file}";
        try
        {
            var stream = await storage.GetAsync(objectKey, ct);
            Response.Headers.CacheControl = "public, max-age=3600";
            return File(stream, "image/webp", enableRangeProcessing: true);
        }
        catch (FileNotFoundException)
        {
            return Problem(title: "Изображение не найдено", statusCode: StatusCodes.Status404NotFound);
        }
    }
}
