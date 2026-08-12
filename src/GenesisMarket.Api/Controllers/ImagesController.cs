using GenesisMarket.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Публичная прокси-отдача картинок объявлений из приватного бакета MinIO. Единственная
/// внешняя точка доступа к фото: бакет остаётся приватным, наружу светится только API.
/// Объекты неизменяемы (ключ — серверный guid), поэтому кэшируются агрессивно и «навсегда».
/// Rate-limit отключён: это, по сути, статика (браузер кэширует), а глобальный лимит
/// зарезал бы загрузку страниц с несколькими картинками.
/// </summary>
[Route("api/images")]
[AllowAnonymous]
[DisableRateLimiting]
public class ImagesController(IObjectStorage storage) : ApiControllerBase
{
    // Все объекты объявлений лежат под этим префиксом; ключи вне него не обслуживаем.
    private const string ListingsPrefix = "listings/";

    [HttpGet("{**key}")]
    public async Task<IActionResult> Get(string key, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(key) || !key.StartsWith(ListingsPrefix, StringComparison.Ordinal))
            return Problem(title: "Изображение не найдено", statusCode: StatusCodes.Status404NotFound);

        var stream = await storage.TryGetAsync(key, ct);
        if (stream is null)
            return Problem(title: "Изображение не найдено", statusCode: StatusCodes.Status404NotFound);

        // Неизменяемый объект: год кэша + immutable — повторных запросов от браузера не будет.
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return File(stream, "image/webp");
    }
}
