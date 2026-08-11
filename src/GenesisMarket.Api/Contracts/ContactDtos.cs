namespace GenesisMarket.Api.Contracts;

/// <summary>
/// Контакты продавца. Возвращаются ТОЛЬКО эндпоинтом
/// <c>GET /api/listings/{id}/contact</c> и нигде больше.
/// Deeplink'и строятся на сервере; null — если канал у продавца выключен.
/// </summary>
public record SellerContactResponse(
    string Phone,
    string? TelegramUrl,
    string? ViberUrl,
    string? WhatsappUrl);
