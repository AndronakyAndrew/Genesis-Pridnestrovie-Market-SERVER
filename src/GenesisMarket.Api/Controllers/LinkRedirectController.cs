using GenesisMarket.Api.Listings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Короткие ссылки для внешних площадок: пост в Telegram-канале ведёт сюда, сервер учитывает
/// переход и отдаёт 302 на карточку. Публичный, вне rate-limit (включая глобальный лимит на IP):
/// переход живого человека из канала не должен упираться в 429. Защиту БД от повторов даёт
/// окно дедупликации кликов (<see cref="ListingOptions.ClickThrottleSeconds"/>).
/// </summary>
[AllowAnonymous]
[DisableRateLimiting]
[ApiExplorerSettings(IgnoreApi = true)]
public class LinkRedirectController(ILinkRedirectService redirects) : ApiControllerBase
{
    /// <summary>
    /// Активное объявление — запись перехода и 302 на карточку с UTM-метками;
    /// нет или снято — 302 на главную. Ошибка записи перехода редирект не отменяет.
    /// HEAD (curl -I, чекеры ссылок) получает тот же 302, но переход не пишется: это не человек.
    /// HEAD обязан быть в списке методов: иначе запрос уходит в служебный endpoint «405»
    /// без AllowAnonymous, и FallbackPolicy отвечает 401.
    /// </summary>
    [AcceptVerbs("GET", "HEAD", Route = "/r/l/{listingId:guid}")]
    public async Task<IActionResult> Listing(
        Guid listingId, [FromQuery(Name = "s")] string? source, CancellationToken ct)
    {
        var target = await redirects.ResolveListingAsync(
            listingId, source, ClientIp(), recordClick: !HttpMethods.IsHead(Request.Method), ct);

        // Кешированный редирект (браузер, edge) прошёл бы мимо сервера — и мимо учёта перехода.
        Response.Headers.CacheControl = "no-store";
        return Redirect(target);
    }
}
