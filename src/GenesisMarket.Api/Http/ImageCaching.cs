using System.Security.Cryptography;
using System.Text;
using Microsoft.Net.Http.Headers;

namespace GenesisMarket.Api.Http;

/// <summary>
/// Кеширование картинок, отдаваемых API из MinIO.
/// <para>
/// ETag выводится из ключа объекта в хранилище, а не из его байтов. Это законно,
/// потому что ключ генерируется сервером один раз при загрузке
/// (<c>listings/{listingId}/{guidv7}.webp</c>, <c>avatars/{userId}/{guidv7}.webp</c>)
/// и никогда не перезаписывается: замена картинки — это всегда новый ключ, а удаление
/// снимает объект целиком. Значит «ключ тот же» ⇒ «байты те же».
/// </para>
/// <para>
/// Выигрыш в том, что ревалидация не стоит ни одного обращения к MinIO: ETag считается
/// из строки, и на <c>If-None-Match</c> сразу уходит 304 без чтения объекта.
/// Если инвариант неизменяемости ключа когда-нибудь нарушат (запись поверх
/// существующего ключа), ETag начнёт врать — тогда считать его придётся от содержимого.
/// </para>
/// </summary>
public static class ImageCaching
{
    /// <summary>
    /// Год — максимум, который имеет смысл указывать (RFC 9111 §5.2.2.1).
    /// <c>immutable</c> убирает ревалидацию даже при перезагрузке страницы.
    /// Ставить только на URL, содержимое которого не может измениться.
    /// </summary>
    public const string Immutable = "public, max-age=31536000, immutable";

    /// <summary>ETag по ключу объекта: 128 бит SHA-256 в hex, сильный валидатор.</summary>
    public static EntityTagHeaderValue ETagFor(string objectKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(objectKey));
        return new EntityTagHeaderValue($"\"{Convert.ToHexString(hash, 0, 16).ToLowerInvariant()}\"");
    }

    /// <summary>
    /// Проверка <c>If-None-Match</c> по правилам RFC 9110 §13.1.2: список тегов через
    /// запятую, <c>*</c> совпадает с любым, сравнение — слабое.
    /// </summary>
    public static bool IsNotModified(HttpRequest request, EntityTagHeaderValue etag)
    {
        var header = request.Headers.IfNoneMatch;
        if (header.Count == 0)
            return false;

        if (!EntityTagHeaderValue.TryParseList(header, out var candidates) || candidates is null)
            return false;

        foreach (var candidate in candidates)
        {
            if (candidate.Equals(EntityTagHeaderValue.Any))
                return true;

            if (candidate.Compare(etag, useStrongComparison: false))
                return true;
        }

        return false;
    }
}
