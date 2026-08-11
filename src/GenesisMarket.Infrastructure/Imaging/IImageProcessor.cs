namespace GenesisMarket.Infrastructure.Imaging;

/// <summary>Результат обработки: перекодированные в WebP оригинал и превью + размеры оригинала.</summary>
public sealed record ProcessedImage(byte[] Original, byte[] Thumbnail, int Width, int Height);

/// <summary>
/// Обработка загруженного изображения: валидация по содержимому, снятие EXIF,
/// ресайз и перекодирование в WebP. Реализация — поверх ImageSharp.
/// </summary>
public interface IImageProcessor
{
    /// <summary>
    /// Проверяет и обрабатывает поток. Кидает <see cref="UnsupportedImageFormatException"/>
    /// для не-изображений и запрещённых форматов, <see cref="ImageTooLargeException"/> —
    /// при превышении лимита пикселей.
    /// </summary>
    Task<ProcessedImage> ProcessAsync(Stream input, CancellationToken ct = default);
}
