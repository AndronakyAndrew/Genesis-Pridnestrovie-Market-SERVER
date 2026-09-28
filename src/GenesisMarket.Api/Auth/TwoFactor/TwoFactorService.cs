using System.Security.Cryptography;
using GenesisMarket.Api.Audit;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Auth.TwoFactor;

public sealed record TwoFactorStatus(bool Enabled, bool SetupPending, int RecoveryCodesLeft);

public sealed record TwoFactorSetup(string Secret, string ProvisioningUri);

public enum SecondFactorResult
{
    Invalid,
    Totp,
    RecoveryCode,

    /// <summary>Второй фактор временно заблокирован после серии неверных кодов — код не проверялся.</summary>
    Locked
}

/// <summary>
/// Второй фактор: настройка TOTP, проверка кода при входе, резервные коды. Все
/// изменения пишутся в журнал действий. Сохранение — внутри методов: каждое действие
/// самостоятельно и не участвует во внешних транзакциях.
/// </summary>
public sealed class TwoFactorService(
    AppDbContext db,
    ITwoFactorSecretProtector protector,
    IAuditLog auditLog,
    IOptions<TwoFactorOptions> options,
    TimeProvider clock)
{
    // Без похожих символов (0/O, 1/I/L): код вводят с бумаги.
    private const string RecoveryAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
    private const int RecoveryCodeLength = 10;

    private readonly TwoFactorOptions _o = options.Value;

    public async Task<TwoFactorStatus> GetStatusAsync(Guid userId, CancellationToken ct)
    {
        var row = await db.TwoFactors.AsNoTracking().FirstOrDefaultAsync(t => t.UserId == userId, ct);
        var left = row?.IsEnabled == true
            ? await db.RecoveryCodes.CountAsync(c => c.UserId == userId && c.UsedAt == null, ct)
            : 0;
        return new TwoFactorStatus(row?.IsEnabled == true, row is { IsEnabled: false }, left);
    }

    public Task<bool> IsEnabledAsync(Guid userId, CancellationToken ct) =>
        db.TwoFactors.AnyAsync(t => t.UserId == userId && t.EnabledAt != null, ct);

    /// <summary>
    /// Начать настройку: новый секрет, пока не действующий. Повторный вызов заменяет
    /// незавершённый секрет (человек потерял QR — просто начинает заново). null — 2FA
    /// уже включена: перенастройка только через отключение или сброс.
    /// </summary>
    public async Task<TwoFactorSetup?> BeginSetupAsync(Guid userId, string accountLabel, CancellationToken ct)
    {
        var row = await db.TwoFactors.FirstOrDefaultAsync(t => t.UserId == userId, ct);
        if (row?.IsEnabled == true)
            return null;

        var secret = Totp.NewSecret();
        if (row is null)
            db.TwoFactors.Add(new UserTwoFactor { UserId = userId, SecretCipher = protector.Protect(secret) });
        else
        {
            row.SecretCipher = protector.Protect(secret);
            row.LastUsedTimeStep = 0;
        }

        await db.SaveChangesAsync(ct);
        return new TwoFactorSetup(Base32.Encode(secret), Totp.ProvisioningUri(_o.Issuer, accountLabel, secret));
    }

    /// <summary>
    /// Подтвердить настройку первым кодом. Возвращает пачку резервных кодов (показываются
    /// один раз) или null, если настройки нет или код неверен.
    /// </summary>
    public async Task<IReadOnlyList<string>?> EnableAsync(Guid userId, string code, CancellationToken ct)
    {
        var row = await db.TwoFactors.FirstOrDefaultAsync(t => t.UserId == userId && t.EnabledAt == null, ct);
        if (row is null)
            return null;

        var step = Totp.Verify(protector.Unprotect(row.SecretCipher), code, clock.GetUtcNow(), row.LastUsedTimeStep);
        if (step is null)
            return null;

        row.EnabledAt = clock.GetUtcNow();
        row.LastUsedTimeStep = step.Value;
        var codes = await ReplaceRecoveryCodesAsync(userId, ct);

        auditLog.Record(AuditActions.TwoFactorEnabled, AuditActions.TargetUser, userId.ToString(),
            oldValue: new { enabled = false }, newValue: new { enabled = true });

        await db.SaveChangesAsync(ct);
        return codes;
    }

    /// <summary>
    /// Проверка второго фактора: TOTP либо резервный код. Успешный TOTP сдвигает
    /// последний принятый шаг, резервный код гасится и пишется в журнал.
    /// </summary>
    public async Task<SecondFactorResult> VerifyAsync(Guid userId, string? code, CancellationToken ct)
    {
        var row = await db.TwoFactors.FirstOrDefaultAsync(t => t.UserId == userId && t.EnabledAt != null, ct);
        if (row is null || string.IsNullOrWhiteSpace(code))
            return SecondFactorResult.Invalid;

        var now = clock.GetUtcNow();
        if (IsLocked(row, now))
            return SecondFactorResult.Locked;

        var step = Totp.Verify(protector.Unprotect(row.SecretCipher), code, now, row.LastUsedTimeStep);
        if (step is not null)
        {
            row.LastUsedTimeStep = step.Value;
            ResetFailures(row);
            await db.SaveChangesAsync(ct);
            return SecondFactorResult.Totp;
        }

        if (NormalizeRecovery(code) is not { } normalized)
            return await RegisterFailureAsync(row, now, ct);

        var hash = protector.HashRecoveryCode(normalized);

        // Условный UPDATE: один код не проходит дважды даже при двух одновременных входах.
        var used = await db.RecoveryCodes
            .Where(c => c.UserId == userId && c.CodeHash == hash && c.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now), ct);
        if (used == 0)
            return await RegisterFailureAsync(row, now, ct);

        ResetFailures(row);
        var left = await db.RecoveryCodes.CountAsync(c => c.UserId == userId && c.UsedAt == null, ct);
        // Вход ещё не завершён — исполнитель указан явно: это сам владелец аккаунта.
        auditLog.Record(AuditActions.RecoveryCodeUsed, AuditActions.TargetUser, userId.ToString(),
            newValue: new { recoveryCodesLeft = left }, actingUserId: userId);
        await db.SaveChangesAsync(ct);

        return SecondFactorResult.RecoveryCode;
    }

    /// <summary>Новая пачка резервных кодов (старая гасится целиком). Требует действующий TOTP.</summary>
    public async Task<IReadOnlyList<string>?> RegenerateRecoveryCodesAsync(Guid userId, string code, CancellationToken ct)
    {
        var row = await db.TwoFactors.FirstOrDefaultAsync(t => t.UserId == userId && t.EnabledAt != null, ct);
        if (row is null)
            return null;

        // Та же блокировка по аккаунту: перевыпуск кодов — тоже проверка второго фактора.
        var now = clock.GetUtcNow();
        if (IsLocked(row, now))
            return null;

        var step = Totp.Verify(protector.Unprotect(row.SecretCipher), code, now, row.LastUsedTimeStep);
        if (step is null)
        {
            await RegisterFailureAsync(row, now, ct);
            return null;
        }

        row.LastUsedTimeStep = step.Value;
        ResetFailures(row);
        var codes = await ReplaceRecoveryCodesAsync(userId, ct);
        auditLog.Record(AuditActions.RecoveryCodesRegenerated, AuditActions.TargetUser, userId.ToString(),
            newValue: new { recoveryCodes = codes.Count });

        await db.SaveChangesAsync(ct);
        return codes;
    }

    /// <summary>Отключить 2FA самому (только не сотруднику — проверяет вызывающий). Требует действующий код.</summary>
    public async Task<bool> DisableAsync(Guid userId, string code, CancellationToken ct)
    {
        // Только явный успех: и неверный код, и блокировка — отказ.
        if (await VerifyAsync(userId, code, ct) is not (SecondFactorResult.Totp or SecondFactorResult.RecoveryCode))
            return false;

        await RemoveAsync(userId, ct);
        auditLog.Record(AuditActions.TwoFactorDisabled, AuditActions.TargetUser, userId.ToString(),
            oldValue: new { enabled = true }, newValue: new { enabled = false });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Аварийный сброс (CLI): удалить 2FA без кода. Вызывающий отзывает сессии —
    /// сброс без выхода оставил бы живыми сессии того, кто мог завладеть устройством.
    /// </summary>
    public async Task<bool> ResetAsync(Guid userId, string? reason, CancellationToken ct)
    {
        var existed = await db.TwoFactors.AnyAsync(t => t.UserId == userId, ct);
        if (!existed)
            return false;

        await RemoveAsync(userId, ct);
        auditLog.Record(AuditActions.TwoFactorReset, AuditActions.TargetUser, userId.ToString(),
            oldValue: new { enabled = true }, newValue: new { enabled = false }, reason: reason);
        return true;
    }

    /// <summary>До какого момента второй фактор заблокирован; null — не заблокирован.</summary>
    public async Task<DateTimeOffset?> LockedUntilAsync(Guid userId, CancellationToken ct)
    {
        var until = await db.TwoFactors.AsNoTracking()
            .Where(t => t.UserId == userId)
            .Select(t => t.LockedUntil)
            .FirstOrDefaultAsync(ct);
        return until is { } u && u > clock.GetUtcNow() ? u : null;
    }

    private static bool IsLocked(UserTwoFactor row, DateTimeOffset now) => row.LockedUntil is { } u && u > now;

    private static void ResetFailures(UserTwoFactor row)
    {
        row.FailedAttempts = 0;
        row.LockedUntil = null;
    }

    /// <summary>
    /// Неверный код: счётчик по аккаунту (с любых адресов и билетов). На пороге — блокировка
    /// второго фактора на <see cref="TwoFactorOptions.LockoutMinutes"/> и запись в журнал.
    /// Исполнитель — владелец аккаунта явно: вход ещё не завершён, токена нет.
    /// </summary>
    private async Task<SecondFactorResult> RegisterFailureAsync(UserTwoFactor row, DateTimeOffset now, CancellationToken ct)
    {
        row.FailedAttempts++;
        if (row.FailedAttempts >= Math.Max(1, _o.LockoutThreshold))
        {
            row.LockedUntil = now.AddMinutes(Math.Max(1, _o.LockoutMinutes));
            auditLog.Record(AuditActions.TwoFactorLocked, AuditActions.TargetUser, row.UserId.ToString(),
                newValue: new { failedAttempts = row.FailedAttempts, lockedUntil = row.LockedUntil },
                actingUserId: row.UserId);
            row.FailedAttempts = 0;
        }
        await db.SaveChangesAsync(ct);
        return SecondFactorResult.Invalid;
    }

    private async Task RemoveAsync(Guid userId, CancellationToken ct)
    {
        await db.RecoveryCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
        await db.TwoFactors.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);
    }

    private async Task<IReadOnlyList<string>> ReplaceRecoveryCodesAsync(Guid userId, CancellationToken ct)
    {
        await db.RecoveryCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);

        var codes = new List<string>(_o.RecoveryCodeCount);
        for (var i = 0; i < _o.RecoveryCodeCount; i++)
        {
            var raw = RandomNumberGenerator.GetString(RecoveryAlphabet, RecoveryCodeLength);
            codes.Add($"{raw[..5]}-{raw[5..]}");
            db.RecoveryCodes.Add(new RecoveryCode { UserId = userId, CodeHash = protector.HashRecoveryCode(raw) });
        }
        return codes;
    }

    /// <summary>Регистр, пробелы и дефис не важны: «abcde-fghjk» == «ABCDEFGHJK».</summary>
    private static string? NormalizeRecovery(string code)
    {
        var s = new string(code.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();
        return s.Length == RecoveryCodeLength && s.All(RecoveryAlphabet.Contains) ? s : null;
    }
}
