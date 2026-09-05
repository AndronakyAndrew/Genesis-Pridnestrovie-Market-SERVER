namespace GenesisMarket.Api.Auth;

/// <summary>
/// Чтение встроенных ресурсов писем (HTML-шаблоны, логотип). Ресурсы лежат
/// в сборке Api (см. <c>EmbeddedResource</c> в csproj), поэтому файлов рядом
/// с бинарником не требуется.
/// </summary>
internal static class EmailResources
{
    public static string LoadText(string fileName)
    {
        using var stream = Open(fileName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static byte[] LoadBytes(string fileName)
    {
        using var stream = Open(fileName);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static Stream Open(string fileName)
    {
        var asm = typeof(EmailResources).Assembly;
        var name = Array.Find(
            asm.GetManifestResourceNames(),
            n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Встроенный ресурс не найден: {fileName}");
        return asm.GetManifestResourceStream(name)!;
    }
}
