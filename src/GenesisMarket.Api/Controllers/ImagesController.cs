using System.Text.RegularExpressions;
using GenesisMarket.Api.Http;
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
        var etag = ImageCaching.ETagFor(objectKey);

        // Ревалидация обслуживается без обращения к MinIO: ключ неизменяем, поэтому
        // совпадения ETag достаточно. Побочный эффект: клиент со старым ETag получит
        // 304 и на уже удалённую картинку — но её URL к тому моменту не отдаёт ни одна
        // ручка, так что промах дешевле похода в хранилище на каждую ревалидацию.
        if (ImageCaching.IsNotModified(Request, etag))
        {
            Response.Headers.CacheControl = ImageCaching.Immutable;
            Response.Headers.ETag = etag.ToString();
            return StatusCode(StatusCodes.Status304NotModified);
        }

        try
        {
            var stream = await storage.GetAsync(objectKey, ct);
            // Ключ содержит GUIDv7, выданный при загрузке, и никогда не перезаписывается:
            // новая картинка — всегда новый ключ. Поэтому URL кешируется навсегда.
            Response.Headers.CacheControl = ImageCaching.Immutable;
            return File(stream, "image/webp", lastModified: null, entityTag: etag,
                enableRangeProcessing: true);
        }
        catch (FileNotFoundException)
        {
            // Cache-Control выставляется только на успешном пути: кешировать 404 на год нельзя.
            return Problem(title: "Изображение не найдено", statusCode: StatusCodes.Status404NotFound);
        }
    }
}
