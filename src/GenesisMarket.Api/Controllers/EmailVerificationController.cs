using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Подтверждение электронной почты кодом — из профиля. Тот же механизм, что и
/// у телефона. На проде подтверждение почты обязательно для публикации объявлений.
/// </summary>
[Authorize]
[Route("api/me/email")]
public class EmailVerificationController(VerificationService verification)
    : VerificationControllerBase(verification)
{
    [HttpPost("send-code")]
    public Task<IActionResult> SendCode(CancellationToken ct) =>
        SendCodeAsync(VerificationChannel.Email, ct);

    [HttpPost("verify")]
    public Task<IActionResult> Verify(VerifyCodeRequest request, CancellationToken ct) =>
        VerifyAsync(VerificationChannel.Email, request, ct);
}
