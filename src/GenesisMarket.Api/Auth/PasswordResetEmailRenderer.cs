namespace GenesisMarket.Api.Auth;

/// <summary>
/// Рендер письма с ссылкой восстановления пароля. Шаблон и логотип читаются
/// один раз при старте (встроенные ресурсы); логотип вшивается через cid,
/// поэтому хостинг картинки не нужен.
/// </summary>
public sealed class PasswordResetEmailRenderer
{
    private const string LogoContentId = "gm-logo";
    private const string Subject = "Местная площадка Genesis — восстановление пароля";

    private readonly string _htmlTemplate = EmailResources.LoadText("password-reset.html");
    private readonly byte[] _logo = EmailResources.LoadBytes("gm-logo.png");

    public RenderedEmail RenderResetEmail(string resetUrl, int ttlMinutes)
    {
        var html = _htmlTemplate
            .Replace("{{RESET_URL}}", resetUrl)
            .Replace("{{TTL}}", ttlMinutes.ToString())
            .Replace("{{LOGO_URL}}", $"cid:{LogoContentId}");

        var text =
            "Местная площадка Genesis — восстановление пароля\n\n" +
            "Чтобы задать новый пароль, откройте ссылку:\n" +
            $"{resetUrl}\n\n" +
            $"Ссылка действует {ttlMinutes} мин. и срабатывает один раз.\n" +
            "Никому её не пересылайте.\n\n" +
            "Если вы не запрашивали смену пароля — просто проигнорируйте это письмо, " +
            "пароль останется прежним.";

        return new RenderedEmail(
            Subject,
            html,
            text,
            new InlineImage(LogoContentId, _logo, "image/png"));
    }
}
