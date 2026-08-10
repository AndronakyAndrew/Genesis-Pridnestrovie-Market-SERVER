using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Подтверждение телефона по SMS — из профиля. Код генерируется сервером.
/// (Сейчас телефон не обязателен для публикации — см. политику Publishing.)
/// </summary>
[Authorize]
[Route("api/me/phone")]
public class PhoneVerificationController(VerificationService verification)
    : VerificationControllerBase(verification)
{
    [HttpPost("send-code")]
    public Task<IActionResult> SendCode(CancellationToken ct) =>
        SendCodeAsync(VerificationChannel.Phone, ct);

    [HttpPost("verify")]
    public Task<IActionResult> Verify(VerifyCodeRequest request, CancellationToken ct) =>
        VerifyAsync(VerificationChannel.Phone, request, ct);
}
