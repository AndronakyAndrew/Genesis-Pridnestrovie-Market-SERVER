namespace GenesisMarket.Api.Feedback;

/// <summary>Настройки Resend. Секция <c>Resend</c>. ApiKey — только из env.</summary>
public sealed class ResendOptions
{
    public const string Section = "Resend";

    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Адрес отправителя (например <c>Местная площадка Genesis &lt;noreply@genesis-market.com&gt;</c>).
    /// ВАЖНО: домен должен быть подтверждён в панели Resend (Domains → Add Domain, DNS-записи
    /// SPF/DKIM/DMARC) — без этого Resend отклонит отправку. Дефолтный адрес вида
    /// <c>onboarding@resend.dev</c> подходит только для разработки, не для прода.
    /// </summary>
    public string FromEmail { get; set; } = "";

    /// <summary>Служебный адрес, на который приходят уведомления о новых обращениях формы.</summary>
    public string NotificationEmail { get; set; } = "";
}
