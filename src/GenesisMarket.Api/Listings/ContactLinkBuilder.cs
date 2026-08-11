using GenesisMarket.Api.Contracts;

namespace GenesisMarket.Api.Listings;

/// <summary>
/// Строит deeplink'и мессенджеров из телефона (E.164) и настроек профиля.
/// Телефон приходит уже нормализованным (<c>+373...</c>); наружу отдаётся как есть,
/// а в ссылках — без ведущего «+».
/// </summary>
public static class ContactLinkBuilder
{
    public static SellerContactResponse Build(
        string phoneE164, string? telegramUsername, bool viberEnabled, bool whatsappEnabled)
    {
        // wa.me и viber ждут только цифры; «+» кодируется как %2B в viber.
        var digits = phoneE164.TrimStart('+');

        var telegramUrl = string.IsNullOrWhiteSpace(telegramUsername)
            ? null
            : $"https://t.me/{telegramUsername.TrimStart('@')}";

        var viberUrl = viberEnabled ? $"viber://chat?number=%2B{digits}" : null;
        var whatsappUrl = whatsappEnabled ? $"https://wa.me/{digits}" : null;

        return new SellerContactResponse(phoneE164, telegramUrl, viberUrl, whatsappUrl);
    }
}
