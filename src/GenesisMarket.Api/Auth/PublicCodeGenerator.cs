using System.Security.Cryptography;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Auth;

/// <summary>
/// Источник сырого пятизначного числа. Вынесен отдельным интерфейсом ровно
/// затем, чтобы в тестах подменять случайность детерминированной
/// последовательностью и проверять поведение при коллизии.
/// </summary>
public interface IPublicCodeSource
{
    /// <summary>Пять цифр, «10000»–«99999».</summary>
    string Next();
}

/// <summary>Криптослучайный источник: <see cref="RandomNumberGenerator"/>, не <c>Random</c>.</summary>
public sealed class RandomPublicCodeSource : IPublicCodeSource
{
    public string Next() =>
        RandomNumberGenerator.GetInt32(PublicCode.Min, PublicCode.Max + 1)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Выдаёт свободный публичный номер аккаунта.</summary>
public interface IPublicCodeGenerator
{
    Task<string> NextAsync(CancellationToken ct);
}

/// <summary>Границы диапазона — одно место на весь проект.</summary>
public static class PublicCode
{
    public const int Min = 10_000;
    public const int Max = 99_999;
    public const int Length = 5;

    /// <summary>Сколько кодов вообще существует. См. комментарий в <see cref="PublicCodeGenerator"/>.</summary>
    public const int PoolSize = Max - Min + 1;
}

/// <summary>
/// Тянет случайные коды, пока не найдёт свободный.
///
/// Предел ёмкости: кодов всего 90 000. Вероятность исчерпать все
/// <see cref="MaxAttempts"/> попыток — (N/90000)^10, где N — число аккаунтов:
/// при 45 000 это одна регистрация из тысячи, при 72 000 — каждая десятая.
/// То есть диапазон становится проблемой задолго до формального исчерпания;
/// когда до этого дойдёт, расширять надо разрядность, а не число попыток.
/// </summary>
public sealed class PublicCodeGenerator(AppDbContext db, IPublicCodeSource source) : IPublicCodeGenerator
{
    public const int MaxAttempts = 10;

    public async Task<string> NextAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var code = source.Next();
            if (!await db.Users.AnyAsync(u => u.PublicCode == code, ct))
                return code;
        }

        throw new PublicCodeExhaustedException(MaxAttempts);
    }
}

/// <summary>
/// Свободный код не найден за отведённое число попыток. Отдельный тип, а не
/// <c>InvalidOperationException</c>: это сигнал «диапазон переполнен, нужна
/// разрядность», и в логе он должен читаться именно так.
/// </summary>
public sealed class PublicCodeExhaustedException(int attempts)
    : Exception($"Не удалось подобрать свободный публичный код аккаунта за {attempts} попыток.");
