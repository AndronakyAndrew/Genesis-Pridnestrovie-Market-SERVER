using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace GenesisMarket.Infrastructure.Imaging;

/// <summary>
/// Обработка изображений на ImageSharp. Порядок принципиален:
/// 1) тип определяется по содержимому (magic bytes), не по расширению/Content-Type;
/// 2) размеры проверяются по заголовку ДО полного декодирования (защита от бомбы);
/// 3) применяется EXIF-ориентация, затем метаданные полностью снимаются (в EXIF — GPS);
/// 4) ресайз и перекодирование в WebP.
/// </summary>
public sealed class ImageSharpImageProcessor : IImageProcessor
{
    private const int MaxLongSide = 1600;      // длинная сторона оригинала
    private const int ThumbWidth = 400;
    private const int ThumbHeight = 300;
    private const int Quality = 82;            // WebP quality
    private const long MaxPixels = 50_000_000; // предел до декодирования

    // format.Name у ImageSharp: "JPEG" / "PNG" / "WEBP".
    private static readonly HashSet<string> AllowedFormats =
        new(StringComparer.OrdinalIgnoreCase) { "JPEG", "PNG", "WEBP" };

    static ImageSharpImageProcessor()
    {
        // Ограничиваем единичную аллокацию декодера — второй рубеж против
        // decompression bomb (первый — проверка объявленных размеров до декода).
        Configuration.Default.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions
        {
            AllocationLimitMegabytes = 512
        });
    }

    public async Task<ProcessedImage> ProcessAsync(Stream input, CancellationToken ct = default)
    {
        if (!input.CanSeek)
            throw new ArgumentException("Ожидается перематываемый поток", nameof(input));

        // 1) Тип — строго по содержимому.
        IImageFormat? format;
        try
        {
            input.Position = 0;
            format = await Image.DetectFormatAsync(input, ct);
        }
        catch (UnknownImageFormatException)
        {
            throw new UnsupportedImageFormatException();
        }

        if (format is null || !AllowedFormats.Contains(format.Name))
            throw new UnsupportedImageFormatException();

        // 2) Размеры — по заголовку, до полного декодирования.
        ImageInfo info;
        try
        {
            input.Position = 0;
            info = await Image.IdentifyAsync(input, ct);
        }
        catch (ImageFormatException)
        {
            throw new UnsupportedImageFormatException();
        }

        if ((long)info.Width * info.Height > MaxPixels)
            throw new ImageTooLargeException();

        // 3) Декодирование.
        Image<Rgba32> image;
        try
        {
            input.Position = 0;
            image = await Image.LoadAsync<Rgba32>(input, ct);
        }
        catch (ImageFormatException)
        {
            throw new UnsupportedImageFormatException();
        }

        using (image)
        {
            // Ориентацию из EXIF применяем ДО снятия метаданных, иначе фото развернётся.
            image.Mutate(x => x.AutoOrient());

            // Полностью снимаем метаданные: EXIF/IPTC/XMP — там геолокация продавца.
            image.Metadata.ExifProfile = null;
            image.Metadata.IptcProfile = null;
            image.Metadata.XmpProfile = null;

            var encoder = new WebpEncoder { Quality = Quality, FileFormat = WebpFileFormatType.Lossy };

            var original = await EncodeResizedAsync(image, MaxLongSide, encoder, ct);
            var thumbnail = await EncodeCropAsync(image, ThumbWidth, ThumbHeight, encoder, ct);

            return new ProcessedImage(original.Bytes, thumbnail, original.Width, original.Height);
        }
    }

    /// <summary>Ужимает до <paramref name="maxSide"/> по длинной стороне (только вниз) и кодирует в WebP.</summary>
    private static async Task<(byte[] Bytes, int Width, int Height)> EncodeResizedAsync(
        Image<Rgba32> source, int maxSide, WebpEncoder encoder, CancellationToken ct)
    {
        using var clone = source.Clone(ctx =>
        {
            if (Math.Max(source.Width, source.Height) > maxSide)
                ctx.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(maxSide, maxSide) });
        });

        using var ms = new MemoryStream();
        await clone.SaveAsync(ms, encoder, ct);
        return (ms.ToArray(), clone.Width, clone.Height);
    }

    /// <summary>Превью фиксированного размера, кроп «cover» из центра, кодирование в WebP.</summary>
    private static async Task<byte[]> EncodeCropAsync(
        Image<Rgba32> source, int width, int height, WebpEncoder encoder, CancellationToken ct)
    {
        using var clone = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Crop,
            Size = new Size(width, height),
            Position = AnchorPositionMode.Center
        }));

        using var ms = new MemoryStream();
        await clone.SaveAsync(ms, encoder, ct);
        return ms.ToArray();
    }
}
