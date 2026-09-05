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

    private readonly string _htmlTemplate = EmailResources.LoadText("verification-code.html");
    private readonly byte[] _logo = EmailResources.LoadBytes("gm-logo.png");

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
}
