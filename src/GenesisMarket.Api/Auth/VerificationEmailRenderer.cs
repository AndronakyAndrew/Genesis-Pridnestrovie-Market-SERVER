namespace GenesisMarket.Api.Auth;

public sealed record RenderedEmail(string Subject, string Html, string Text, InlineImage? Logo);

/// <summary>
/// Рендер письма с кодом: HTML-шаблон и логотип читаются один раз при старте
/// (встроенные ресурсы) и кешируются; подстановка — обычный Replace.
/// Логотип вшивается в письмо через cid, поэтому хостинг картинки не нужен.
/// Шаблон общий для подтверждения почты и входа по коду — отличаются только тексты.
/// </summary>
public sealed class VerificationEmailRenderer
{
    private const string LogoContentId = "gm-logo";
    private const int CodeTtlMinutes = 5;

    private readonly string _htmlTemplate = EmailResources.LoadText("verification-code.html");
    private readonly byte[] _logo = EmailResources.LoadBytes("gm-logo.png");

    private sealed record Copy(string Title, string Heading, string Hint, string Ignore);

    private static readonly Copy Confirm = new(
        "Код подтверждения",
        "Подтверждение адреса почты",
        "Введите этот код на странице регистрации",
        "Если вы не регистрировались на нашей площадке, просто проигнорируйте это письмо — никаких действий не требуется.");

    private static readonly Copy Login = new(
        "Код для входа",
        "Вход в аккаунт",
        "Введите этот код на странице входа",
        "Если вы не пытались войти, просто проигнорируйте это письмо — без кода войти в аккаунт нельзя.");

    private static readonly Copy EmailChange = new(
        "Смена email",
        "Подтверждение нового адреса почты",
        "Введите этот код в настройках аккаунта",
        "Если вы не запрашивали смену email, проигнорируйте это письмо — адрес не изменится.");

    private static readonly Copy AccountDeletion = new(
        "Удаление аккаунта",
        "Подтверждение удаления аккаунта",
        "Введите этот код на странице подтверждения удаления",
        "Если вы не запрашивали удаление аккаунта, проигнорируйте это письмо.");

    public RenderedEmail RenderCodeEmail(string code) => Render(code, Confirm);

    public RenderedEmail RenderLoginCodeEmail(string code) => Render(code, Login);

    public RenderedEmail RenderEmailChangeCodeEmail(string code) => Render(code, EmailChange);

    public RenderedEmail RenderAccountDeletionCodeEmail(string code) => Render(code, AccountDeletion);

    private RenderedEmail Render(string code, Copy copy)
    {
        var html = _htmlTemplate
            .Replace("{{TITLE}}", copy.Title)
            .Replace("{{HEADING}}", copy.Heading)
            .Replace("{{HINT}}", copy.Hint)
            .Replace("{{IGNORE}}", copy.Ignore)
            .Replace("{{CODE}}", code)
            .Replace("{{LOGO_URL}}", $"cid:{LogoContentId}");

        var text =
            $"Местная площадка Genesis — {copy.Title.ToLowerInvariant()}\n\n" +
            $"Ваш код: {code}\n" +
            $"Действует {CodeTtlMinutes} минут.\n\n" +
            $"Никому не сообщайте этот код. {copy.Ignore}";

        return new RenderedEmail(
            $"Местная площадка Genesis — {copy.Title.ToLowerInvariant()}",
            html,
            text,
            new InlineImage(LogoContentId, _logo, "image/png"));
    }
}
