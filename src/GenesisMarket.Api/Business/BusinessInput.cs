using System.Text.RegularExpressions;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Listings;
using GenesisMarket.Domain.Entities;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Business;

/// <summary>
/// Нормализация и проверка реквизитов. Одна точка правды и для валидатора запроса,
/// и для повторной проверки номера на подаче.
/// </summary>
public static partial class BusinessInput
{
    public const int ShopNameMin = 2;
    public const int ShopNameMax = 80;
    public const int PickupAddressMin = 5;
    public const int PickupAddressMax = 200;
    public const int RegistrationNumberMax = 32;
    public const int RejectionReasonMax = 500;

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Whitespace();

    // Дефисы и тире, которые приносит копипаст из документов: ‐ ‑ ‒ – — −.
    [GeneratedRegex(@"[‐-―−]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex DashLike();

    /// <summary>Текстовое поле: пробелы по краям сняты, внутренние схлопнуты. null → "".</summary>
    public static string NormalizeText(string? input) =>
        input is null ? "" : Whitespace().Replace(input.Trim(), " ");

    /// <summary>
    /// Регистрационный номер: без пробелов, любые тире → «-», верхний регистр.
    /// «01 – 023 – 4567» и «01-023-4567» — один номер: на нормализованном значении
    /// держится уникальный индекс подтверждённых номеров.
    /// </summary>
    public static string NormalizeRegistrationNumber(string? input) =>
        input is null ? "" : DashLike().Replace(Whitespace().Replace(input, ""), "-").ToUpperInvariant();

    /// <summary>Контакт в тексте (телефон, ссылка, ник, почта) — та же функция, что режет описания объявлений.</summary>
    public static bool ContainsContacts(string text) => ListingContentRisk.RedactContacts(text) != text;

    public static BusinessDetails ToDetails(SaveBusinessDetailsRequest r) => new(
        NormalizeText(r.ShopName),
        r.LegalForm!.Value,
        NormalizeRegistrationNumber(r.RegistrationNumber),
        NormalizeText(r.PickupAddress));
}

/// <summary>Скомпилированный шаблон регистрационного номера из <see cref="BusinessOptions"/>.</summary>
public sealed class RegistrationNumberRule(IOptions<BusinessOptions> options)
{
    private readonly Regex _pattern = new(
        options.Value.RegistrationNumberPattern,
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>Номер (уже нормализованный) соответствует формату.</summary>
    public bool IsValid(string normalized)
    {
        if (normalized.Length is 0 or > BusinessInput.RegistrationNumberMax)
            return false;
        try
        {
            return _pattern.IsMatch(normalized);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
