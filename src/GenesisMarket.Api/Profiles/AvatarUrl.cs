using Microsoft.AspNetCore.Http;

namespace GenesisMarket.Api.Profiles;

/// <summary>
/// Сборка публичного адреса аватара. Одно место на весь проект: адрес строят
/// три контроллера (профиль, свой кабинет, отзывы), и раньше каждый склеивал
/// строку сам — так GUID владельца попадал в разметку с трёх сторон.
/// Адресуем по «ID профиля»: Guid — это UUID v7, то есть время регистрации
/// открытым текстом, и в публичной ссылке ему не место.
/// </summary>
public static class AvatarUrl
{
    public static string Build(HttpRequest request, string publicCode, DateTimeOffset? updatedAt) =>
        $"{request.Scheme}://{request.Host}/api/users/by-code/{publicCode}/avatar?v={updatedAt?.UtcTicks ?? 0}";
}
