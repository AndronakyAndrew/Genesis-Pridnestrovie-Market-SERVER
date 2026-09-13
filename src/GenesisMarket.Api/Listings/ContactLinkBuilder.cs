using GenesisMarket.Api.Contracts;

namespace GenesisMarket.Api.Listings;

/// <summary>
/// Строит deeplink'и мессенджеров из телефона (E.164) и настроек профиля.
/// Телефон приходит уже нормализованным (<c>+373...</c>); наружу отдаётся как есть,
/// а в ссылках — без ведущего «+».
/// </summary>
public static class ContactLinkBuilder
{
    /// <param name="phoneE164">
    /// Номер, который разрешено показать, или null — продавец номер скрыл (или не задал).
    /// Без номера Viber и WhatsApp не строятся: <c>wa.me/{digits}</c> и
    /// <c>viber://chat?number=…</c> несут номер в открытом виде.
    /// </param>
    public static SellerContactResponse Build(
        string? phoneE164, string? telegramUsername, bool viberEnabled, bool whatsappEnabled)
    {
        var telegramUrl = string.IsNullOrWhiteSpace(telegramUsername)
            ? null
            : $"https://t.me/{telegramUsername.TrimStart('@')}";

        if (string.IsNullOrEmpty(phoneE164))
            return new SellerContactResponse(null, telegramUrl, null, null);

        // wa.me и viber ждут только цифры; «+» кодируется как %2B в viber.
        var digits = phoneE164.TrimStart('+');

        var viberUrl = viberEnabled ? $"viber://chat?number=%2B{digits}" : null;
        var whatsappUrl = whatsappEnabled ? $"https://wa.me/{digits}" : null;

        return new SellerContactResponse(phoneE164, telegramUrl, viberUrl, whatsappUrl);
    }
}
