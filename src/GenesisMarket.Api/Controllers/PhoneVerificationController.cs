using System.Security.Cryptography;
using System.Text;
using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Подтверждение телефона по SMS — из профиля, не при регистрации.
/// Код генерируется сервером и отправляется напрямую от Genesis Market.
/// Без подтверждения телефона нельзя публиковать объявления.
/// </summary>
[Authorize]
[Route("api/me/phone")]
public class PhoneVerificationController(
    AppDbContext db,
    ISmsSender sms,
    IOptions<PhoneVerificationOptions> options) : ApiControllerBase
{
    private readonly PhoneVerificationOptions _o = options.Value;

    [HttpPost("send-code")]
    public async Task<ActionResult<SendCodeResponse>> SendCode(CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound);

        if (user.PhoneVerified)
            return Problem(title: "Телефон уже подтверждён", statusCode: StatusCodes.Status409Conflict);

        if (string.IsNullOrEmpty(user.PhoneE164))
            return Problem(title: "Телефон не указан", statusCode: StatusCodes.Status400BadRequest);

        // Кулдаун на повторную отправку.
        var last = await db.PhoneVerificationCodes
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (last is not null)
        {
            var elapsed = DateTimeOffset.UtcNow - last.CreatedAt;
            var cooldown = TimeSpan.FromSeconds(_o.ResendCooldownSeconds);
            if (elapsed < cooldown)
            {
                Response.Headers.RetryAfter = ((int)Math.Ceiling((cooldown - elapsed).TotalSeconds)).ToString();
                return Problem(title: "Код уже отправлен, подождите перед повторной отправкой",
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
        }

        var code = GenerateCode(_o.CodeLength);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_o.CodeTtlMinutes);

        db.PhoneVerificationCodes.Add(new PhoneVerificationCode
        {
            UserId = userId,
            Phone = user.PhoneE164,
            CodeHash = Hash(code),
            ExpiresAt = expiresAt
        });
        await db.SaveChangesAsync(ct);

        await sms.SendAsync(
            user.PhoneE164,
            $"Genesis Market: код подтверждения {code}. Действует {_o.CodeTtlMinutes} мин.",
            ct);

        return Ok(new SendCodeResponse("Код отправлен по SMS.", expiresAt));
    }

    [HttpPost("verify")]
    public async Task<ActionResult<MessageResponse>> Verify(VerifyPhoneRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound);

        if (user.PhoneVerified)
            return Problem(title: "Телефон уже подтверждён", statusCode: StatusCodes.Status409Conflict);

        var code = await db.PhoneVerificationCodes
            .Where(c => c.UserId == userId && c.ConsumedAt == null)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (code is null)
            return Problem(title: "Код не запрашивался", statusCode: StatusCodes.Status400BadRequest);

        if (DateTimeOffset.UtcNow >= code.ExpiresAt)
            return Problem(title: "Код истёк, запросите новый", statusCode: StatusCodes.Status400BadRequest);

        if (code.Attempts >= _o.MaxAttempts)
            return Problem(title: "Превышено число попыток, запросите новый код",
                statusCode: StatusCodes.Status400BadRequest);

        // Постоянное по времени сравнение хешей.
        if (!CryptographicOperations.FixedTimeEquals(Hash(request.Code), code.CodeHash))
        {
            code.Attempts++;
            await db.SaveChangesAsync(ct);
            return Problem(title: "Неверный код", statusCode: StatusCodes.Status400BadRequest);
        }

        user.PhoneVerified = true;
        code.ConsumedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Ok(new MessageResponse("Телефон подтверждён."));
    }

    private static string GenerateCode(int length)
    {
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
            sb.Append((char)('0' + RandomNumberGenerator.GetInt32(0, 10)));
        return sb.ToString();
    }

    private static byte[] Hash(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim()));
}
