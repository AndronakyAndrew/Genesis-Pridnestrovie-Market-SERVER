using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GenesisMarket.Api.Listings;
using GenesisMarket.Api.Trust;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Moderation;

/// <summary>
/// Номера карт: нормализация, хеш и поиск в тексте. Номер нигде не хранится и не
/// логируется — наружу выходят только HMAC и последние 4 цифры.
/// </summary>
public static partial class CardNumbers
{
    public const int MinDigits = 13;
    public const int MaxDigits = 19;

    // Цифры с одиночными пробелами/дефисами между ними: «9005 1234 5678 1124»,
    // «9005-1234-5678-1124», «9005123456781124». Маскированные («9005 **** 1124») не
    // ловятся — по ним нечего сверять. Luhn не проверяем намеренно: местные карты
    // («Клевер» и др.) у нас не сверены с алгоритмом, а ложных срабатываний здесь
    // быть не может — совпадение только с хешем из чёрного списка.
    [GeneratedRegex(@"(?<!\d)(?:\d[ \-]?){12,18}\d(?!\d)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex CandidatePattern();

    /// <summary>Только цифры номера или null, если длина не похожа на карту.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;
        var digits = new string(input.Where(char.IsAsciiDigit).ToArray());
        return digits.Length is >= MinDigits and <= MaxDigits ? digits : null;
    }

    /// <summary>Все похожие на номер карты последовательности в тексте (нормализованные).</summary>
    public static IReadOnlyList<string> Extract(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        try
        {
            return CandidatePattern().Matches(text)
                .Select(m => Normalize(m.Value))
                .OfType<string>()
                .Distinct()
                .ToList();
        }
        catch (RegexMatchTimeoutException)
        {
            return [];
        }
    }
}

/// <summary>HMAC номера карты. Отдельный интерфейс — чтобы в журналы не утёк сам номер.</summary>
public interface ICardHasher
{
    /// <summary>HMAC-SHA256 (hex) нормализованных цифр; null — ключ хеширования не задан.</summary>
    string? Hash(string digits);
}

/// <summary>
/// Ключ — тот же <c>Security:IpHashKey</c> (секрет только из env), но с разделением
/// доменов: хешируется «card:» + цифры. Отдельный секрет ради одного списка не
/// заводим — разделение доменов не даёт хешу карты совпасть с хешем IP.
/// Без ключа (dev) чёрный список карт выключен: хранить номер открыто нельзя.
/// </summary>
public sealed class HmacCardHasher : ICardHasher
{
    private readonly byte[]? _key;

    public HmacCardHasher(IConfiguration configuration)
    {
        var key = configuration["Security:IpHashKey"];
        _key = string.IsNullOrEmpty(key) ? null : Encoding.UTF8.GetBytes(key);
    }

    public string? Hash(string digits)
    {
        if (_key is null)
            return null;
        using var hmac = new HMACSHA256(_key);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes("card:" + digits)));
    }
}

/// <summary>
/// Применение чёрного списка к публикации: карта из списка в заголовке или описании
/// — жёсткое правило сильнее доверия автора, как стоп-слово. Объявление уходит на
/// премодерацию с приоритетом автофлага жалоб (Trust:AutoFlagPriority) — выше любой
/// риск-оценки.
/// </summary>
public interface ICardBlocklist
{
    Task<PublishDecision> EnforceAsync(PublishDecision decision, string title, string description, CancellationToken ct);
}

public sealed class CardBlocklist(
    AppDbContext db,
    ICardHasher hasher,
    IOptions<TrustOptions> trust,
    ILogger<CardBlocklist> logger) : ICardBlocklist
{
    public async Task<PublishDecision> EnforceAsync(
        PublishDecision decision, string title, string description, CancellationToken ct)
    {
        var hashes = CardNumbers.Extract(title).Concat(CardNumbers.Extract(description))
            .Select(hasher.Hash)
            .OfType<string>()
            .Distinct()
            .ToList();
        if (hashes.Count == 0)
            return decision;

        var hit = await db.BlockedCards.AsNoTracking()
            .Where(c => hashes.Contains(c.CardHash))
            .Select(c => new { c.Id, c.Last4 })
            .FirstOrDefaultAsync(ct);
        if (hit is null)
            return decision;

        // В лог — id записи и последние цифры, но не номер.
        logger.LogWarning("Публикация с картой из чёрного списка (запись {BlockedCardId}, …{Last4}) → премодерация.",
            hit.Id, hit.Last4);

        return decision with
        {
            Mode = PublishMode.PreReview,
            Priority = Math.Max(decision.Priority, trust.Value.AutoFlagPriority),
            Reason = $"карта из чёрного списка (…{hit.Last4}); {decision.Reason}"
        };
    }
}
