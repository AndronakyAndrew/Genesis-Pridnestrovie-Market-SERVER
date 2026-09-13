namespace GenesisMarket.Api.Contracts;

/// <summary>
/// Контакты продавца. Возвращаются ТОЛЬКО эндпоинтом
/// <c>GET /api/listings/{id}/contact</c> и нигде больше.
/// Deeplink'и строятся на сервере; null — если канал у продавца выключен.
/// <para>
/// <see cref="Phone"/> = null, когда продавец снял «Показывать телефон в объявлениях».
/// Тогда <see cref="ViberUrl"/> и <see cref="WhatsappUrl"/> тоже null: их ссылки
/// содержат сам номер, и отдать их значило бы раскрыть скрытый телефон.
/// Остаётся только <see cref="TelegramUrl"/> — он строится из username, не из номера.
/// </para>
/// </summary>
public record SellerContactResponse(
    string? Phone,
    string? TelegramUrl,
    string? ViberUrl,
    string? WhatsappUrl);
