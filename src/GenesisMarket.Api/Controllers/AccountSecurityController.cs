using GenesisMarket.Api.Accounts;
using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Auth.Roles;
using GenesisMarket.Api.Auth.TwoFactor;
using GenesisMarket.Api.Business;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Controllers;

[Authorize]
[Route("api/account")]
public sealed class AccountSecurityController(
    AppDbContext db,
    VerificationService verification,
    IRefreshTokenService refreshTokens,
    SecurityStampValidator securityStamp,
    ITokenService tokenService,
    IRoleStore roles,
    TwoFactorService twoFactor,
    IAccountDeletionService accountDeletion,
    IEmailSender emailSender,
    ILogger<AccountSecurityController> logger) : ApiControllerBase
{
    private const string NeutralEmailMessage = "Если адрес можно использовать, код отправлен на него.";

    [HttpPost("email-change/send-code")]
    public async Task<IActionResult> SendEmailChangeCode(EmailChangeSendCodeRequest request, CancellationToken ct)
    {
        if (!TryNormalizeEmail(request.NewEmail, out var newEmail))
            return ProblemWithCode(StatusCodes.Status400BadRequest, "invalid_email", "Укажите корректный email.");

        var userId = CurrentUserId()!.Value;
        var currentEmail = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId && !u.IsDeleted)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(ct);
        if (currentEmail is null)
            return Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound);

        if (string.Equals(currentEmail, newEmail, StringComparison.OrdinalIgnoreCase))
            return ProblemWithCode(StatusCodes.Status409Conflict, "email_same_as_current", "Этот email уже используется аккаунтом.");

        // Одинаковый ответ при свободном и занятом адресе не раскрывает аккаунты.
        var taken = await db.Users.AsNoTracking()
            .AnyAsync(u => !u.IsDeleted && u.Id != userId && u.Email == newEmail, ct);
        if (taken)
            return Ok(new MessageResponse(NeutralEmailMessage));

        var sent = await verification.SendToTargetAsync(userId, VerificationChannel.EmailChange, newEmail, ct);
        return sent.Status switch
        {
            SendStatus.Ok => Ok(new MessageResponse(NeutralEmailMessage)),
            SendStatus.Cooldown => TooManyRequestsWithCode(
                sent.RetryAfterSeconds, "verification_code_cooldown", "Подождите перед повторной отправкой кода."),
            SendStatus.ChannelUnavailable => ProblemWithCode(
                StatusCodes.Status503ServiceUnavailable, "email_delivery_unavailable", "Почта временно недоступна."),
            _ => Problem(title: "Не удалось отправить код", statusCode: StatusCodes.Status400BadRequest)
        };
    }

    [HttpPost("email-change/verify")]
    public async Task<IActionResult> VerifyEmailChange(
        EmailChangeVerifyRequest request, CancellationToken ct)
    {
        if (!TryNormalizeEmail(request.NewEmail, out var newEmail))
            return ProblemWithCode(StatusCodes.Status400BadRequest, "invalid_email", "Укажите корректный email.");

        if (CurrentUser.SessionId is not { } sessionId)
            return ProblemWithCode(StatusCodes.Status409Conflict, "session_refresh_required", "Обновите сессию и повторите запрос.");

        var userId = CurrentUserId()!.Value;
        var currentEmail = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId && !u.IsDeleted)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(ct);
        if (currentEmail is null)
            return Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound);

        if (string.Equals(currentEmail, newEmail, StringComparison.OrdinalIgnoreCase))
            return ProblemWithCode(StatusCodes.Status409Conflict, "email_same_as_current", "Этот email уже используется аккаунтом.");

        var verified = await verification.VerifyTargetAsync(
            userId, VerificationChannel.EmailChange, newEmail, request.Code, ct);
        if (verified.Status is not VerifyStatus.Ok)
            return VerificationProblem(verified.Status, "email_change");

        var nextStamp = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            if (await db.Users.AsNoTracking().AnyAsync(
                    u => !u.IsDeleted && u.Id != userId && u.Email == newEmail, ct))
                return ProblemWithCode(StatusCodes.Status409Conflict, "email_already_in_use", "Этот email уже занят.");

            var changed = await db.Users
                .Where(u => u.Id == userId && !u.IsDeleted && u.Email == currentEmail)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.Email, newEmail)
                    .SetProperty(u => u.EmailVerified, true)
                    .SetProperty(u => u.SecurityStamp, nextStamp)
                    .SetProperty(u => u.UpdatedAt, now), ct);

            if (changed == 0)
                return ProblemWithCode(StatusCodes.Status409Conflict, "email_change_conflict", "Email аккаунта уже изменился. Обновите страницу.");

            await verification.InvalidateAsync(userId, ct,
                VerificationChannel.Email,
                VerificationChannel.LoginEmail,
                VerificationChannel.EmailChange,
                VerificationChannel.AccountDeletion);
            await db.PasswordResetTokens
                .Where(t => t.UserId == userId && t.ConsumedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAt, now), ct);
            await refreshTokens.RevokeOtherSessionsAsync(userId, sessionId, ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex) when (ex.IsUniqueViolation())
        {
            return ProblemWithCode(StatusCodes.Status409Conflict, "email_already_in_use", "Этот email уже занят.");
        }

        securityStamp.Invalidate(userId);
        try
        {
            await emailSender.SendAsync(currentEmail,
                "Изменён email аккаунта Genesis",
                "<p>Адрес электронной почты вашего аккаунта Genesis был изменён.</p><p>Если это сделали не вы, немедленно восстановите доступ к аккаунту.</p>",
                "Адрес электронной почты вашего аккаунта Genesis был изменён.\nЕсли это сделали не вы, немедленно восстановите доступ к аккаунту.",
                inlineImage: null, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось отправить уведомление о смене email на старый адрес. UserId={UserId}", userId);
        }

        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        var access = tokenService.CreateAccessToken(
            user, await roles.GetRoleAsync(userId, ct), sessionId, CurrentUser.MfaVerified);
        return Ok(new EmailChangeResponse(user.Email, user.EmailVerified, access.Value, access.ExpiresAt));
    }

    [HttpPost("delete/send-code")]
    public async Task<ActionResult<AccountDeleteSendCodeResponse>> SendAccountDeleteCode(CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;
        var email = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId && !u.IsDeleted)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(ct);
        if (email is null)
            return Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound);

        var result = await verification.SendToTargetAsync(userId, VerificationChannel.AccountDeletion, email, ct);
        return result.Status switch
        {
            SendStatus.Ok => Ok(new AccountDeleteSendCodeResponse("Код отправлен на email аккаунта.", result.ExpiresAt!.Value)),
            SendStatus.Cooldown => TooManyRequestsWithCode(
                result.RetryAfterSeconds, "verification_code_cooldown", "Подождите перед повторной отправкой кода."),
            SendStatus.ChannelUnavailable => ProblemWithCode(
                StatusCodes.Status503ServiceUnavailable, "email_delivery_unavailable", "Почта временно недоступна."),
            _ => Problem(title: "Не удалось отправить код", statusCode: StatusCodes.Status400BadRequest)
        };
    }

    [HttpPost("delete/confirm")]
    public async Task<IActionResult> ConfirmAccountDelete(AccountDeleteConfirmRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;
        var email = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId && !u.IsDeleted)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(ct);
        if (email is null)
            return Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound);

        if (await twoFactor.IsEnabledAsync(userId, ct))
        {
            if (string.IsNullOrWhiteSpace(request.TwoFactorCode))
                return ProblemWithCode(StatusCodes.Status400BadRequest, "second_factor_required", "Введите код 2FA.");

            var secondFactor = await twoFactor.VerifyAsync(userId, request.TwoFactorCode, ct);
            if (secondFactor == SecondFactorResult.Locked)
                return ProblemWithCode(StatusCodes.Status429TooManyRequests, "second_factor_locked", "Проверка 2FA временно заблокирована.");
            if (secondFactor is not (SecondFactorResult.Totp or SecondFactorResult.RecoveryCode))
                return ProblemWithCode(StatusCodes.Status400BadRequest, "invalid_second_factor", "Код 2FA неверен.");
        }

        var verified = await verification.VerifyTargetAsync(
            userId, VerificationChannel.AccountDeletion, email, request.Code, ct);
        if (verified.Status is not VerifyStatus.Ok)
            return VerificationProblem(verified.Status, "account_delete");

        return await accountDeletion.DeleteAsync(userId, ct)
            ? NoContent()
            : Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound);
    }

    private IActionResult VerificationProblem(VerifyStatus status, string prefix) => status switch
    {
        VerifyStatus.Expired => ProblemWithCode(StatusCodes.Status410Gone, $"{prefix}_code_expired", "Срок действия кода истёк."),
        VerifyStatus.TooManyAttempts => ProblemWithCode(StatusCodes.Status429TooManyRequests, $"{prefix}_code_attempts_exceeded", "Превышено число попыток. Запросите новый код."),
        VerifyStatus.Invalid => ProblemWithCode(StatusCodes.Status400BadRequest, $"{prefix}_code_invalid", "Код неверен."),
        VerifyStatus.NoCode => ProblemWithCode(StatusCodes.Status400BadRequest, $"{prefix}_code_not_found", "Активный код не найден. Запросите новый."),
        VerifyStatus.AlreadyVerified => ProblemWithCode(StatusCodes.Status409Conflict, $"{prefix}_already_verified", "Действие уже выполнено."),
        _ => ProblemWithCode(StatusCodes.Status400BadRequest, $"{prefix}_code_invalid", "Код неверен.")
    };

    private ObjectResult TooManyRequestsWithCode(int retryAfterSeconds, string code, string title)
    {
        Response.Headers.RetryAfter = Math.Max(retryAfterSeconds, 1).ToString();
        return ProblemWithCode(StatusCodes.Status429TooManyRequests, code, title);
    }

    private ObjectResult ProblemWithCode(int status, string code, string title)
    {
        var problem = new ProblemDetails { Status = status, Title = title };
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status };
    }

    private static bool TryNormalizeEmail(string raw, out string normalized)
    {
        normalized = raw.Trim().ToLowerInvariant();
        return new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(normalized);
    }
}
