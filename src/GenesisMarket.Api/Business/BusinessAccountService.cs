using GenesisMarket.Api.Contracts;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Business;

/// <summary>Результат операции владельца: профиль после изменения либо ошибка.</summary>
public sealed record BusinessResult(BusinessProfile? Profile, BusinessError? Error = null, bool VerificationReset = false)
{
    public static BusinessResult Fail(BusinessError error) => new(null, error);
}

/// <summary>
/// Переходы бизнес-режима со стороны владельца. Здесь — ВСЕ правила: контроллер
/// только переводит результат в HTTP. Решений модератора тут нет вовсе
/// (<see cref="BusinessVerificationService"/>): ни один путь этого сервиса не
/// приводит к Verified или Rejected.
/// </summary>
public interface IBusinessAccountService
{
    Task<BusinessProfile?> FindAsync(Guid userId, CancellationToken ct);

    /// <summary>Сохранить реквизиты и включить режим. Реквизиты уже провалидированы и нормализованы.</summary>
    Task<BusinessResult> SaveAsync(Guid userId, BusinessDetails details, CancellationToken ct);

    /// <summary>Отправить на проверку (→ Pending).</summary>
    Task<BusinessResult> SubmitAsync(Guid userId, CancellationToken ct);

    /// <summary>Вернуться в Private (Verified снимается, Pending отзывается).</summary>
    Task<BusinessResult> DisableAsync(Guid userId, CancellationToken ct);

    /// <summary>Ответ владельцу с вычисленными CanEdit/CanSubmit/NextSubmitAllowedAt.</summary>
    BusinessAccountResponse ToResponse(BusinessProfile? profile, bool verificationReset = false);
}

public sealed class BusinessAccountService(
    AppDbContext db,
    RegistrationNumberRule registrationNumber,
    IOptions<BusinessOptions> options,
    TimeProvider clock) : IBusinessAccountService
{
    public Task<BusinessProfile?> FindAsync(Guid userId, CancellationToken ct) =>
        db.BusinessProfiles.FirstOrDefaultAsync(b => b.UserId == userId, ct);

    public async Task<BusinessResult> SaveAsync(Guid userId, BusinessDetails details, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var profile = await FindAsync(userId, ct);

        if (profile is null)
        {
            profile = BusinessProfile.Create(userId, details, now);
            db.BusinessProfiles.Add(profile);
            return await SaveOrConflictAsync(profile, verificationReset: false, ct);
        }

        if (!profile.CanEditDetails)
            return BusinessResult.Fail(new BusinessError(StatusCodes.Status409Conflict,
                BusinessErrorCodes.DetailsLocked,
                "Реквизиты на проверке у модератора — изменить их сейчас нельзя"));

        var reset = profile.UpdateDetails(details, now);
        return await SaveOrConflictAsync(profile, reset, ct);
    }

    public async Task<BusinessResult> SubmitAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var profile = await FindAsync(userId, ct);

        // Переход в Business без реквизитов невозможен: строка появляется только
        // с полным набором реквизитов, а режим включается их сохранением.
        if (profile is null || profile.AccountType != AccountType.Business)
            return BusinessResult.Fail(new BusinessError(StatusCodes.Status409Conflict,
                BusinessErrorCodes.BusinessModeDisabled,
                "Сначала заполните и сохраните реквизиты бизнеса"));

        switch (profile.Status)
        {
            case BusinessVerificationStatus.Pending:
                return BusinessResult.Fail(new BusinessError(StatusCodes.Status409Conflict,
                    BusinessErrorCodes.AlreadyPending, "Заявка уже на проверке"));
            case BusinessVerificationStatus.Verified:
                return BusinessResult.Fail(new BusinessError(StatusCodes.Status409Conflict,
                    BusinessErrorCodes.AlreadyVerified, "Бизнес уже подтверждён"));
        }

        // Как и раскрытие контактов: регистрация сама по себе ничего не подтверждает,
        // и без этого условия очередь модератора наполняется одноразовыми аккаунтами.
        var emailVerified = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.EmailVerified)
            .FirstOrDefaultAsync(ct);
        if (!emailVerified)
            return BusinessResult.Fail(new BusinessError(StatusCodes.Status403Forbidden,
                BusinessErrorCodes.EmailNotVerified,
                "Подтвердите почту, чтобы отправить реквизиты на проверку"));

        if (NextSubmitAllowedAt(profile) is { } allowedAt && allowedAt > now)
            return BusinessResult.Fail(new BusinessError(StatusCodes.Status429TooManyRequests,
                BusinessErrorCodes.ResubmitCooldown,
                "Повторно отправить заявку можно позже",
                RetryAt: allowedAt));

        // Формат — ещё раз: шаблон мог ужесточиться после сохранения реквизитов.
        if (!registrationNumber.IsValid(profile.RegistrationNumber))
            return BusinessResult.Fail(new BusinessError(StatusCodes.Status400BadRequest,
                BusinessErrorCodes.RegistrationNumberFormat,
                "Неверный формат регистрационного номера — исправьте реквизиты",
                Field: "registrationNumber"));

        // Номер уже подтверждён у другого аккаунта — одобрить всё равно не выйдет
        // (уникальный индекс), незачем ставить заявку в очередь.
        var taken = await db.BusinessProfiles.AsNoTracking().AnyAsync(b =>
            b.UserId != userId &&
            b.RegistrationNumber == profile.RegistrationNumber &&
            b.Status == BusinessVerificationStatus.Verified, ct);
        if (taken)
            return BusinessResult.Fail(new BusinessError(StatusCodes.Status409Conflict,
                BusinessErrorCodes.RegistrationNumberTaken,
                "Этот регистрационный номер уже подтверждён у другого аккаунта",
                Field: "registrationNumber"));

        profile.Submit(now);
        return await SaveOrConflictAsync(profile, verificationReset: false, ct);
    }

    public async Task<BusinessResult> DisableAsync(Guid userId, CancellationToken ct)
    {
        var profile = await FindAsync(userId, ct);

        // Реквизитов не было — аккаунт и так частный. Идемпотентно.
        if (profile is null)
            return new BusinessResult(null);

        profile.Disable(clock.GetUtcNow());
        return await SaveOrConflictAsync(profile, verificationReset: false, ct);
    }

    public BusinessAccountResponse ToResponse(BusinessProfile? p, bool verificationReset = false)
    {
        if (p is null)
            return new BusinessAccountResponse(
                AccountType.Private, BusinessVerificationStatus.None, null, null, null, null,
                CanEdit: true, CanSubmit: false, NextSubmitAllowedAt: null);

        var now = clock.GetUtcNow();
        var nextSubmit = NextSubmitAllowedAt(p);
        var canSubmit = p.AccountType == AccountType.Business
                        && (p.Status is BusinessVerificationStatus.None or BusinessVerificationStatus.Rejected)
                        && (nextSubmit is null || nextSubmit <= now);

        return new BusinessAccountResponse(
            p.AccountType,
            p.Status,
            new BusinessDetailsDto(p.ShopName, p.LegalForm, p.RegistrationNumber, p.PickupAddress),
            p.Status == BusinessVerificationStatus.Rejected ? p.RejectionReason : null,
            p.SubmittedAt,
            p.ReviewedAt,
            p.CanEditDetails,
            canSubmit,
            nextSubmit > now ? nextSubmit : null,
            verificationReset);
    }

    /// <summary>
    /// Раньше какого момента подавать нельзя. Два ограничения, оба — по полям в БД:
    /// минимальный интервал между подачами и пауза после отказа, удваивающаяся
    /// с каждым отказом до потолка.
    /// </summary>
    private DateTimeOffset? NextSubmitAllowedAt(BusinessProfile p)
    {
        var o = options.Value;
        DateTimeOffset? result = p.SubmittedAt?.AddMinutes(o.MinMinutesBetweenSubmissions);

        if (p.Status == BusinessVerificationStatus.Rejected && p.ReviewedAt is { } rejectedAt && p.RejectionCount > 0)
        {
            var exponent = Math.Min(p.RejectionCount - 1, 16);   // 2^16 часов заведомо выше любого потолка
            var hours = Math.Min((long)o.ResubmitCooldownHours << exponent, o.MaxResubmitCooldownHours);
            var afterRejection = rejectedAt.AddHours(hours);
            if (result is null || afterRejection > result)
                result = afterRejection;
        }

        return result;
    }

    private async Task<BusinessResult> SaveOrConflictAsync(BusinessProfile profile, bool verificationReset, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return new BusinessResult(profile, VerificationReset: verificationReset);
        }
        // Status — токен конкурентности: параллельный переход (другая вкладка, решение
        // модератора) уже изменил строку. Первое сохранение, вставленное дважды, —
        // нарушение PK. В обоих случаях клиенту: перечитать и повторить.
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.IsUniqueViolation())
        {
            return BusinessResult.Fail(new BusinessError(StatusCodes.Status409Conflict,
                BusinessErrorCodes.ConcurrentChange,
                "Состояние бизнес-аккаунта изменилось — обновите страницу и повторите"));
        }
    }
}
