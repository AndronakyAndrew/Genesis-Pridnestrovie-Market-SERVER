using GenesisMarket.Infrastructure.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Инварианты обработки изображения. Раньше были покрыты только через HTTP
/// (снятие EXIF в <see cref="ListingImagesTests"/>); здесь — напрямую, чтобы
/// подкрутка энкодера не меняла контракт молча.
/// </summary>
public class ImageProcessorTests
{
    private readonly IImageProcessor _processor = new ImageSharpImageProcessor();

    [Fact]
    public async Task Large_photo_is_capped_to_1600_on_the_long_side()
    {
        using var input = new MemoryStream(PhotoJpeg(4000, 3000));

        var result = await _processor.ProcessAsync(input);

        Assert.Equal(1600, result.Width);
        Assert.Equal(1200, result.Height);

        // Заявленные размеры обязаны совпасть с реальными: по ним фронтенд
        // резервирует место под картинку.
        using var original = Image.Load(result.Original);
        Assert.Equal(result.Width, original.Width);
        Assert.Equal(result.Height, original.Height);
    }

    [Fact]
    public async Task Small_image_is_not_upscaled()
    {
        using var input = new MemoryStream(PhotoJpeg(320, 240));

        var result = await _processor.ProcessAsync(input);

        Assert.Equal(320, result.Width);
        Assert.Equal(240, result.Height);
    }

    [Fact]
    public async Task Both_outputs_are_webp_and_thumbnail_is_fixed_size()
    {
        using var input = new MemoryStream(PhotoJpeg(4000, 3000));

        var result = await _processor.ProcessAsync(input);

        Assert.Equal("WEBP", Image.DetectFormat(result.Original).Name, ignoreCase: true);
        Assert.Equal("WEBP", Image.DetectFormat(result.Thumbnail).Name, ignoreCase: true);

        // Превью теперь режется из уже уменьшенной копии — размер обязан остаться тем же.
        using var thumb = Image.Load(result.Thumbnail);
        Assert.Equal(400, thumb.Width);
        Assert.Equal(300, thumb.Height);
    }

    [Fact]
    public async Task Metadata_is_stripped_from_both_outputs()
    {
        using var input = new MemoryStream(JpegWithGps(2000, 1500));

        var result = await _processor.ProcessAsync(input);

        foreach (var bytes in new[] { result.Original, result.Thumbnail })
        {
            using var image = Image.Load(bytes);
            Assert.Null(image.Metadata.ExifProfile);
            Assert.Null(image.Metadata.IptcProfile);
            Assert.Null(image.Metadata.XmpProfile);
        }
    }

    [Fact]
    public async Task Non_image_content_is_rejected()
    {
        using var input = new MemoryStream("<"u8.ToArray().Concat("?php echo 1; ?>"u8.ToArray()).ToArray());

        await Assert.ThrowsAsync<UnsupportedImageFormatException>(() => _processor.ProcessAsync(input));
    }

    [Fact]
    public async Task Pixel_bomb_is_rejected_before_decoding()
    {
        // 8000x8000 = 64 Мпикс > MaxPixels (50 Мпикс).
        using var input = new MemoryStream(PhotoJpeg(8000, 8000));

        await Assert.ThrowsAsync<ImageTooLargeException>(() => _processor.ProcessAsync(input));
    }

    // ---- helpers ----

    private static byte[] PhotoJpeg(int w, int h)
    {
        using var image = new Image<Rgba32>(w, h);
        image.Mutate(x => x.BackgroundColor(Color.SlateGray));
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms);
        return ms.ToArray();
    }

    private static byte[] JpegWithGps(int w, int h)
    {
        using var image = new Image<Rgba32>(w, h);
        image.Mutate(x => x.BackgroundColor(Color.CornflowerBlue));

        var exif = new ExifProfile();
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(46), new Rational(50), new Rational(0)]);
        exif.SetValue(ExifTag.Make, "GenesisTestCam");
        image.Metadata.ExifProfile = exif;

        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms);
        return ms.ToArray();
    }
}
