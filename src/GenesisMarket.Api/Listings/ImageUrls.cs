namespace GenesisMarket.Api.Listings;

/// <summary>
/// Строит публичные URL картинок объявлений на прокси-эндпоинт API
/// (<c>GET /api/images/{key}</c>). Отдаём через собственный домен, а не presigned-ссылки
/// MinIO: одна публичная поверхность (важно для деплоя за одним доменом/ngrok), MinIO
/// наружу не светится и не участвует в подписи. Схема/хост берутся из запроса — за прокси
/// (ForwardedHeaders) это уже публичные адреса.
/// </summary>
public static class ImageUrls
{
    public static string? Build(HttpRequest request, string? objectKey)
    {
        if (string.IsNullOrEmpty(objectKey))
            return null;

        // Ключи безопасны для пути (guid, '/', '.', '_') — дополнительного кодирования не требуют.
        return $"{request.Scheme}://{request.Host}/api/images/{objectKey}";
    }
}
