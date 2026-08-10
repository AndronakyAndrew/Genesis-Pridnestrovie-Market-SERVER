using System.Reflection;

namespace GenesisMarket.Api.Auth;

public sealed record RenderedEmail(string Subject, string Html, string Text, InlineImage? Logo);

/// <summary>
/// Рендер письма с кодом: HTML-шаблон и логотип читаются один раз при старте
/// (встроенные ресурсы) и кешируются; подстановка — обычный Replace.
/// Логотип вшивается в письмо через cid, поэтому хостинг картинки не нужен.
/// </summary>
public sealed class VerificationEmailRenderer
{
    private const string LogoContentId = "gm-logo";
    private const int CodeTtlMinutes = 5;

    private readonly string _htmlTemplate;
    private readonly byte[] _logo;

    public VerificationEmailRenderer()
    {
        _htmlTemplate = LoadText("verification-code.html");
        _logo = LoadBytes("gm-logo.png");
    }

    public RenderedEmail RenderCodeEmail(string code)
    {
        var html = _htmlTemplate
            .Replace("{{CODE}}", code)
            .Replace("{{LOGO_URL}}", $"cid:{LogoContentId}");

        var text =
            $"Genesis Market — код подтверждения\n\n" +
            $"Ваш код: {code}\n" +
            $"Действует {CodeTtlMinutes} минут.\n\n" +
            "Никому не сообщайте этот код. Если вы не регистрировались на Genesis Market — " +
            "просто проигнорируйте это письмо.";

        return new RenderedEmail(
            "Genesis Market — код подтверждения",
            html,
            text,
            new InlineImage(LogoContentId, _logo, "image/png"));
    }

    private static Stream OpenResource(string fileName)
    {
        var asm = typeof(VerificationEmailRenderer).Assembly;
        var name = Array.Find(
            asm.GetManifestResourceNames(),
            n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Встроенный ресурс не найден: {fileName}");
        return asm.GetManifestResourceStream(name)!;
    }

    private static string LoadText(string fileName)
    {
        using var stream = OpenResource(fileName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static byte[] LoadBytes(string fileName)
    {
        using var stream = OpenResource(fileName);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
