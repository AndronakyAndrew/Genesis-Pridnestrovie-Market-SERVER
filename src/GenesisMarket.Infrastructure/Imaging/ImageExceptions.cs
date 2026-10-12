namespace GenesisMarket.Infrastructure.Imaging;

/// <summary>Файл не является изображением или его формат не разрешён.</summary>
public sealed class UnsupportedImageFormatException()
    : Exception("Файл не является изображением поддерживаемого формата (JPEG/JPG, PNG, WebP, HEIC/HEIF)");

/// <summary>Объявленные размеры изображения превышают допустимый предел (защита от decompression bomb).</summary>
public sealed class ImageTooLargeException()
    : Exception("Изображение слишком большое по числу пикселей");
