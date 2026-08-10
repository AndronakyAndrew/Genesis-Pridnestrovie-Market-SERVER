using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Auth;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Собственная аутентификация (JWT + BCrypt). ASP.NET Identity не используется.
/// </summary>
[Route("api/auth")]
public class AuthController(
    AppDbContext db,
    IPasswordHasher hasher,
    IPasswordPolicy passwordPolicy,
    ITokenService tokenService,
    IRefreshTokenService refreshTokens,
    IAuthRateLimiter rateLimiter,
    SecurityStampValidator securityStamp,
    IOptions<PhoneOptions> phoneOptions) : ApiControllerBase
{
    // Один и тот же текст на неверный email и неверный пароль (анти-перечисление).
    private const string LoginError = "Неверный email или пароль";

    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        var ip = ClientIp();
        var limit = rateLimiter.CheckRegister(ip);
        if (!limit.Allowed)
            return TooManyRequests(limit.RetryAfter);

        if (PasswordError(request.Password) is { } pwdError)
            return pwdError;

        var phone = PhoneNumber.Normalize(request.Phone, phoneOptions.Value.AllowOtherCountries);
        if (phone is null)
            return Problem(title: "Некорректный номер телефона", statusCode: StatusCodes.Status400BadRequest);

        var email = Normalize(request.Email);

        // Хеш считаем всегда — чтобы ответ на занятый email не отличался по времени.
        var passwordHash = hasher.Hash(request.Password);

        var exists = await db.Users.AnyAsync(u => u.Email == email, ct);
        if (!exists)
        {
            var user = new User
            {
                Email = email,
                PasswordHash = passwordHash,
                Role = UserRole.User,        // роль всегда User, поле из запроса игнорируется
                PhoneE164 = phone,
                PhoneVerified = false,
                Profile = new Profile
                {
                    DisplayName = request.DisplayName,
                    City = request.City
                }
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
        }

        // Ответ идентичен и для свободного, и для занятого email.
        return Ok(new MessageResponse(
            "Если адрес свободен, аккаунт создан. Теперь вы можете войти."));
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var ip = ClientIp();
        var email = Normalize(request.Email);

        var limit = rateLimiter.CheckLogin(ip, email);
        if (!limit.Allowed)
            return TooManyRequests(limit.RetryAfter);

        var user = await db.Users
            .Include(u => u.Profile)
            .FirstOrDefaultAsync(u => u.Email == email, ct);

        if (user is null)
        {
            // Пользователя нет — всё равно проверяем против фиктивного хеша (timing).
            hasher.Verify(request.Password, hasher.DummyHash);
            return Problem(title: LoginError, statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!hasher.Verify(request.Password, user.PasswordHash))
            return Problem(title: LoginError, statusCode: StatusCodes.Status401Unauthorized);

        // Пересчёт хеша при смене workFactor.
        if (hasher.NeedsRehash(user.PasswordHash))
            user.PasswordHash = hasher.Hash(request.Password);

        return Ok(await IssueAsync(user, ip, ct));
    }

    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh(RefreshRequest request, CancellationToken ct)
    {
        var ip = ClientIp();
        var outcome = await refreshTokens.RotateAsync(request.RefreshToken, ip, ct);

        if (outcome.Status is RefreshStatus.Invalid or RefreshStatus.Reuse)
            return Problem(title: "Недействительный refresh-токен", statusCode: StatusCodes.Status401Unauthorized);

        var user = await db.Users
            .Include(u => u.Profile)
            .FirstOrDefaultAsync(u => u.Id == outcome.UserId, ct);
        if (user is null)
            return Problem(title: "Недействительный refresh-токен", statusCode: StatusCodes.Status401Unauthorized);

        var access = tokenService.CreateAccessToken(user);
        return Ok(new AuthResponse(
            access.Value, access.ExpiresAt,
            outcome.NewRawToken!, outcome.ExpiresAt!.Value,
            MapUser(user)));
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(LogoutRequest request, CancellationToken ct)
    {
        // Отзываем только переданный токен. Результат намеренно не раскрываем.
        await refreshTokens.RevokeAsync(request.RefreshToken, ct);
        return NoContent();
    }

    [Authorize]
    [HttpPost("logout-all")]
    public async Task<IActionResult> LogoutAll(CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;

        await refreshTokens.RevokeAllAsync(userId, ct);

        // Смена SecurityStamp немедленно инвалидирует все выданные access-токены.
        await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, Guid.NewGuid()), ct);

        securityStamp.Invalidate(userId);
        return NoContent();
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound);

        if (!hasher.Verify(request.CurrentPassword, user.PasswordHash))
            return Problem(title: "Текущий пароль неверен", statusCode: StatusCodes.Status400BadRequest);

        if (PasswordError(request.NewPassword) is { } pwdError)
            return pwdError;

        user.PasswordHash = hasher.Hash(request.NewPassword);
        user.SecurityStamp = Guid.NewGuid();               // разлогинивает все сессии
        await refreshTokens.RevokeAllAsync(userId, ct);
        await db.SaveChangesAsync(ct);

        securityStamp.Invalidate(userId);
        return NoContent();
    }

    // ---- helpers ----

    private async Task<AuthResponse> IssueAsync(User user, string ip, CancellationToken ct)
    {
        var access = tokenService.CreateAccessToken(user);
        // IssueAsync вызывает SaveChanges — заодно сохранит возможный rehash пароля.
        var (refreshRaw, refreshExp) = await refreshTokens.IssueAsync(user.Id, ip, ct);
        return new AuthResponse(access.Value, access.ExpiresAt, refreshRaw, refreshExp, MapUser(user));
    }

    private static UserResponse MapUser(User u) => new(
        u.Id,
        u.Email,
        u.Profile?.DisplayName ?? "",
        u.Profile?.City ?? default,
        u.PhoneE164,
        u.PhoneVerified,
        u.CreatedAt);

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();

    private ObjectResult? PasswordError(string password) => passwordPolicy.Validate(password) switch
    {
        PasswordCheck.TooShort => Problem(
            title: "Пароль должен быть не короче 8 байт", statusCode: StatusCodes.Status400BadRequest),
        PasswordCheck.TooLong => Problem(
            title: "Пароль должен быть не длиннее 72 байт", statusCode: StatusCodes.Status400BadRequest),
        PasswordCheck.TooCommon => Problem(
            title: "Пароль слишком простой, выберите другой", statusCode: StatusCodes.Status400BadRequest),
        _ => null
    };

    private ObjectResult TooManyRequests(TimeSpan retryAfter)
    {
        Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        return Problem(title: "Слишком много запросов, попробуйте позже",
            statusCode: StatusCodes.Status429TooManyRequests);
    }
}
