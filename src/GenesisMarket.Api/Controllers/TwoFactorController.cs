using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Auth.Roles;
using GenesisMarket.Api.Auth.TwoFactor;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Двухфакторная аутентификация своего аккаунта. Для служебных ролей обязательна: без
/// неё сессия не получает claim mfa и служебный раздел отвечает 403
/// <c>mfa_setup_required</c>. Ручки, принимающие код, ограничены тем же лимитом, что и
/// вход (5 попыток / 15 минут), — иначе украденный access-токен позволял бы перебирать
/// TOTP здесь.
/// </summary>
[Authorize]
[Route("api/me/2fa")]
public class TwoFactorController(
    AppDbContext db,
    TwoFactorService twoFactor,
    IRoleStore roles,
    IRefreshTokenService refreshTokens,
    ITokenService tokenService,
    IAuthRateLimiter rateLimiter) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<TwoFactorStatusResponse>> GetStatus(CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;
        var status = await twoFactor.GetStatusAsync(userId, ct);
        var required = RolePermissions.IsStaff(await roles.GetRoleAsync(userId, ct));
        return Ok(new TwoFactorStatusResponse(status.Enabled, status.SetupPending, required, status.RecoveryCodesLeft));
    }

    /// <summary>Начать настройку: секрет и otpauth-ссылка. 409 — 2FA уже включена.</summary>
    [HttpPost("setup")]
    public async Task<ActionResult<TwoFactorSetupResponse>> Setup(CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;
        var email = await db.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstAsync(ct);

        var setup = await twoFactor.BeginSetupAsync(userId, email, ct);
        if (setup is null)
            return Problem(title: "Двухфакторная аутентификация уже включена", statusCode: StatusCodes.Status409Conflict);

        return Ok(new TwoFactorSetupResponse(setup.Secret, setup.ProvisioningUri));
    }

    /// <summary>
    /// Подтвердить настройку первым кодом. Остальные сессии аккаунта завершаются: они
    /// открыты без второго фактора. Текущая сессия получает отметку 2FA и новый
    /// access-токен — служебный раздел открывается без повторного входа.
    /// </summary>
    [HttpPost("enable")]
    public async Task<ActionResult<TwoFactorEnabledResponse>> Enable(TwoFactorCodeRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;
        if (Limited(userId) is { } limited)
            return limited;

        var codes = await twoFactor.EnableAsync(userId, request.Code, ct);
        if (codes is null)
            return Problem(title: "Неверный код. Проверьте время на телефоне и попробуйте снова.",
                statusCode: StatusCodes.Status400BadRequest);

        var sessionId = CurrentUser.SessionId ?? Guid.Empty;
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.SessionId != sessionId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTimeOffset.UtcNow), ct);
        await refreshTokens.MarkMfaVerifiedAsync(userId, sessionId, ct);

        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        var access = tokenService.CreateAccessToken(user, await roles.GetRoleAsync(userId, ct), sessionId, mfa: true);

        return Ok(new TwoFactorEnabledResponse(codes, access.Value, access.ExpiresAt));
    }

    /// <summary>Новая пачка резервных кодов; прежние перестают действовать.</summary>
    [HttpPost("recovery-codes")]
    public async Task<ActionResult<RecoveryCodesResponse>> RegenerateRecoveryCodes(
        TwoFactorCodeRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;
        if (Limited(userId) is { } limited)
            return limited;

        var codes = await twoFactor.RegenerateRecoveryCodesAsync(userId, request.Code, ct);
        return codes is null
            ? Problem(title: "Неверный код", statusCode: StatusCodes.Status400BadRequest)
            : Ok(new RecoveryCodesResponse(codes));
    }

    /// <summary>
    /// Отключить 2FA. Сотруднику нельзя: служебная роль без второго фактора не
    /// работает. Сначала понизить роль (владелец) или сбросить 2FA CLI-командой.
    /// </summary>
    [HttpPost("disable")]
    public async Task<IActionResult> Disable(TwoFactorCodeRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;
        if (RolePermissions.IsStaff(await roles.GetRoleAsync(userId, ct)))
            return Problem(title: "Для служебной роли двухфакторная аутентификация обязательна",
                statusCode: StatusCodes.Status403Forbidden);
        if (Limited(userId) is { } limited)
            return limited;

        return await twoFactor.DisableAsync(userId, request.Code, ct)
            ? NoContent()
            : Problem(title: "Неверный код", statusCode: StatusCodes.Status400BadRequest);
    }

    private ObjectResult? Limited(Guid userId)
    {
        var limit = rateLimiter.CheckLogin(ClientIp(), $"2fa:{userId}");
        return limit.Allowed ? null : TooManyRequests((int)Math.Ceiling(limit.RetryAfter.TotalSeconds));
    }
}
