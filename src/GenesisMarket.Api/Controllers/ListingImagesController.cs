using System.Text.Json;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Listings;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Infrastructure.Imaging;
using GenesisMarket.Infrastructure.Persistence;
using GenesisMarket.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Изображения объявления: загрузка (multipart), удаление, изменение порядка, список.
/// Наружу — только DTO с presigned-ссылками. Проверки (владелец, лимиты, формат)
/// выполняются на сервере до любой обработки файла.
/// </summary>
[Route("api/listings/{listingId:guid}/images")]
public class ListingImagesController(
    AppDbContext db,
    IObjectStorage storage,
    IImageProcessor imageProcessor) : ApiControllerBase
{
    private const int MaxImagesPerListing = 8;

    // 10 МБ на файл — предел содержимого.
    private const long MaxImageBytes = 10L * 1024 * 1024;
    // Тот же лимит на уровне запроса/multipart + небольшой запас на обёртку формы.
    private const long MaxRequestBytes = MaxImageBytes + 1024 * 1024;

    /// <summary>Список изображений объявления по порядку. Публично (для карточки объявления).</summary>
    [AllowAnonymous]
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ListingImageResponse>>> GetImages(
        Guid listingId, CancellationToken ct)
    {
        if (!await db.Listings.AnyAsync(l => l.Id == listingId, ct))
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);

        var images = await db.ListingImages
            .AsNoTracking()
            .Where(i => i.ListingId == listingId)
            .OrderBy(i => i.SortOrder)
            .ToListAsync(ct);

        return Ok(MapAll(images));
    }

    /// <summary>
    /// Загрузка одного изображения. Тип определяется по содержимому, EXIF снимается,
    /// файл перекодируется в WebP (оригинал + превью). Не больше 8 на объявление.
    /// </summary>
    [Authorize]
    [HttpPost]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    public async Task<ActionResult<ListingImageResponse>> Upload(
        Guid listingId, IFormFile? file, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;

        // Владелец: фильтр по OwnerId ⇒ и «нет объявления», и «чужое» дают 404.
        var listing = await db.Listings
            .FirstOrDefaultAsync(l => l.Id == listingId && l.OwnerId == userId, ct);
        if (listing is null)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);

        if (file is null || file.Length == 0)
            return Problem(title: "Файл изображения обязателен", statusCode: StatusCodes.Status400BadRequest);

        if (file.Length > MaxImageBytes)
            return Problem(title: "Файл больше 10 МБ", statusCode: StatusCodes.Status413PayloadTooLarge);

        var count = await db.ListingImages.CountAsync(i => i.ListingId == listingId, ct);
        if (count >= MaxImagesPerListing)
            return Problem(title: $"Не больше {MaxImagesPerListing} изображений на объявление",
                statusCode: StatusCodes.Status409Conflict);

        // Буферизуем в память (ограничено лимитом запроса) и обрабатываем.
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);

        ProcessedImage processed;
        try
        {
            processed = await imageProcessor.ProcessAsync(buffer, ct);
        }
        catch (UnsupportedImageFormatException)
        {
            return Problem(title: "Поддерживаются только изображения JPEG, PNG и WebP",
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }
        catch (ImageTooLargeException)
        {
            return Problem(title: "Изображение слишком большое",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Ключи генерирует сервер; имя файла из запроса нигде не используется.
        var key = Guid.CreateVersion7();
        var objectKey = $"listings/{listingId}/{key}.webp";
        var thumbKey = $"listings/{listingId}/{key}_thumb.webp";

        await using (var os = new MemoryStream(processed.Original))
            await storage.PutAsync(objectKey, os, os.Length, "image/webp", ct);
        await using (var ts = new MemoryStream(processed.Thumbnail))
            await storage.PutAsync(thumbKey, ts, ts.Length, "image/webp", ct);

        var maxOrder = await db.ListingImages
            .Where(i => i.ListingId == listingId)
            .Select(i => (int?)i.SortOrder)
            .MaxAsync(ct);

        var image = new ListingImage
        {
            ListingId = listingId,
            ObjectKey = objectKey,
            ThumbKey = thumbKey,
            SortOrder = (maxOrder ?? -1) + 1,
            Width = processed.Width,
            Height = processed.Height
        };
        db.ListingImages.Add(image);
        await db.SaveChangesAsync(ct);

        var dto = Map(image);
        return CreatedAtAction(nameof(GetImages), new { listingId }, dto);
    }

    /// <summary>Удаление изображения. Объекты хранилища удаляются асинхронно через outbox.</summary>
    [Authorize]
    [HttpDelete("{imageId:guid}")]
    public async Task<IActionResult> Delete(Guid listingId, Guid imageId, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;

        var listing = await db.Listings
            .FirstOrDefaultAsync(l => l.Id == listingId && l.OwnerId == userId, ct);
        if (listing is null)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);

        var image = await db.ListingImages
            .FirstOrDefaultAsync(i => i.Id == imageId && i.ListingId == listingId, ct);
        if (image is null)
            return Problem(title: "Изображение не найдено", statusCode: StatusCodes.Status404NotFound);

        db.ListingImages.Remove(image);
        // Удаление объектов — через outbox, не синхронно в обработчике HTTP-запроса.
        // Оригинал и превью — одним сообщением (JSON-массив ключей) в той же транзакции.
        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = OutboxMessage.DeleteImages,
            Payload = JsonSerializer.Serialize(new[] { image.ObjectKey, image.ThumbKey })
        });
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    /// <summary>Изменение порядка. В теле — полный набор Id объявления в нужной последовательности.</summary>
    [Authorize]
    [HttpPatch("order")]
    public async Task<ActionResult<IReadOnlyList<ListingImageResponse>>> Reorder(
        Guid listingId, ReorderImagesRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;

        var listing = await db.Listings
            .FirstOrDefaultAsync(l => l.Id == listingId && l.OwnerId == userId, ct);
        if (listing is null)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);

        var images = await db.ListingImages
            .Where(i => i.ListingId == listingId)
            .ToListAsync(ct);

        var requested = request.ImageIds ?? [];
        // Новый порядок обязан быть перестановкой ровно тех же изображений — без пропусков и дублей.
        if (requested.Count != images.Count ||
            requested.Distinct().Count() != requested.Count ||
            !requested.ToHashSet().SetEquals(images.Select(i => i.Id)))
        {
            return Problem(title: "Список должен содержать все изображения объявления ровно один раз",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var position = new Dictionary<Guid, int>(requested.Count);
        for (var idx = 0; idx < requested.Count; idx++)
            position[requested[idx]] = idx;

        foreach (var img in images)
            img.SortOrder = position[img.Id];
        await db.SaveChangesAsync(ct);

        var ordered = images.OrderBy(i => i.SortOrder).ToList();
        return Ok(MapAll(ordered));
    }

    // ---- helpers ----

    // URL ведут на публичный прокси API (/api/images/{key}); presigned MinIO наружу не отдаём.
    private ListingImageResponse Map(ListingImage i) => new(
        i.Id, i.SortOrder, i.Width, i.Height,
        // ObjectKey/ThumbKey у сохранённого изображения всегда заданы.
        ImageUrls.Build(Request, i.ObjectKey)!, ImageUrls.Build(Request, i.ThumbKey)!);

    private List<ListingImageResponse> MapAll(IEnumerable<ListingImage> images) =>
        images.Select(Map).ToList();
}
