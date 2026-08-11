using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace GenesisMarket.Api.Listings;

/// <summary>
/// Кодек курсора для keyset-пагинации. Курсор — base64url от кортежа
/// (маркер сортировки, значение поля сортировки, Id-тайбрейкер). Id включён
/// в курсор, чтобы порядок был строго детерминирован даже при равных значениях
/// поля сортировки. Маркер сортировки хранится в курсоре и сверяется с запросом:
/// если клиент сменил sort, старый курсор отвергается (400), а не молча ломает порядок.
/// </summary>
public static class CatalogCursor
{
    // Разделитель '|' безопасен: маркер сортировки — латиница/подчёркивание,
    // ключ — цифры/точка/'n'/'-', Id — hex ("N"-формат). Символ '|' в них не встречается.
    private const char Separator = '|';

    public static string Encode(string sortToken, string sortKey, Guid id)
    {
        var raw = $"{sortToken}{Separator}{sortKey}{Separator}{id:N}";
        return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>
    /// Разбирает курсор. Возвращает false при любой некорректности
    /// (битая base64, не тот sort, неверный Id) — вызывающий отвечает 400.
    /// </summary>
    public static bool TryDecode(string cursor, string expectedSortToken, out string sortKey, out Guid id)
    {
        sortKey = string.Empty;
        id = default;
        try
        {
            var raw = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cursor));
            var parts = raw.Split(Separator);
            if (parts.Length != 3 || parts[0] != expectedSortToken)
                return false;
            if (!Guid.TryParseExact(parts[2], "N", out id))
                return false;
            sortKey = parts[1];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
