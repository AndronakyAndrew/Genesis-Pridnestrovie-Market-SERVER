using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Общая логика подтверждения контакта кодом. Конкретные контроллеры
/// (телефон/почта) лишь задают канал и маршрут.
/// </summary>
public abstract class VerificationControllerBase(VerificationService verification) : ApiControllerBase
{
    protected async Task<IActionResult> SendCodeAsync(VerificationChannel channel, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;
        var result = await verification.SendAsync(userId, channel, ct);

        return result.Status switch
        {
            SendStatus.Ok => Ok(new SendCodeResponse("Код отправлен.", result.ExpiresAt!.Value)),
            SendStatus.AlreadyVerified => Problem(title: "Контакт уже подтверждён", statusCode: StatusCodes.Status409Conflict),
            SendStatus.NoTarget => Problem(title: "Контакт не указан", statusCode: StatusCodes.Status400BadRequest),
            SendStatus.Cooldown => TooManyRequests(result.RetryAfterSeconds,
                "Код уже отправлен, подождите перед повторной отправкой"),
            _ => Problem(statusCode: StatusCodes.Status400BadRequest)
        };
    }

    protected async Task<IActionResult> VerifyAsync(
        VerificationChannel channel, VerifyCodeRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;
        var result = await verification.VerifyAsync(userId, channel, request.Code, ct);

        return result.Status switch
        {
            VerifyStatus.Ok => Ok(new MessageResponse("Контакт подтверждён.")),
            VerifyStatus.AlreadyVerified => Problem(title: "Контакт уже подтверждён", statusCode: StatusCodes.Status409Conflict),
            VerifyStatus.NoCode => Problem(title: "Код не запрашивался", statusCode: StatusCodes.Status400BadRequest),
            VerifyStatus.Expired => Problem(title: "Код истёк, запросите новый", statusCode: StatusCodes.Status400BadRequest),
            VerifyStatus.TooManyAttempts => Problem(title: "Превышено число попыток, запросите новый код", statusCode: StatusCodes.Status400BadRequest),
            VerifyStatus.Invalid => Problem(title: "Неверный код", statusCode: StatusCodes.Status400BadRequest),
            _ => Problem(statusCode: StatusCodes.Status400BadRequest)
        };
    }
}
