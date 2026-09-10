using System.Text.RegularExpressions;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Listings;

/// <summary>Что именно нашли в объявлении — для журнала модерации и объяснения решения.</summary>
public sealed record ContentRisk(int Score, IReadOnlyList<string> Signals)
{
    public static readonly ContentRisk None = new(0, []);

    /// <summary>Найдено стоп-слово — жёсткий сигнал, обрабатывается отдельно от score.</summary>
    public bool HasStopWord { get; init; }
}

/// <summary>
/// Дешёвые эвристики по тексту объявления. Никаких внешних сервисов и моделей:
/// цель — не «понять» объявление, а отделить очевидно рисковое от рутины, чтобы
/// доверенный автор не ждал модератора из-за продажи дивана.
///
/// Ложные срабатывания здесь допустимы: они стоят объявлению не отказа, а очереди
/// на проверку. Поэтому пороги подобраны в сторону подозрительности.
/// </summary>
public static partial class ListingContentRisk
{
    // У обеих регулярок таймаут 100 мс: текст пишет пользователь, катастрофического
    // бэктрекинга в шаблонах нет, но таймаут — дешёвая страховка.

    /// <summary>Ссылка наружу: протокол, www, узнаваемый домен, t.me/wa.me или @ник.</summary>
    [GeneratedRegex(
        @"https?://|www\.|\b[a-z0-9][a-z0-9\-]{1,}\.(ru|com|net|org|md|ua|by|kz|info|xyz|top|site|shop|online|store)\b|t\.me/|wa\.me/|@[a-z0-9_]{4,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex ExternalLinkRegex();

    /// <summary>
    /// Телефон в тексте: «+373…», длинный слитный номер или номер с разделителями.
    /// Цену вида «15 000» не ловит — там разряды по 3 цифры с пробелом, а не 8+ подряд.
    /// </summary>
    [GeneratedRegex(
        @"\+\d[\d\s\-()]{7,}|\b\d{8,}\b|\b0\d{3}[\s\-]?\d{2}[\s\-]?\d{2}\b",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex PhoneLikeRegex();

    /// <summary>
    /// Оценка риска объявления. <paramref name="hasImages"/> передаёт вызывающий:
    /// на момент создания черновика фотографий ещё нет, и это не должно штрафоваться.
    /// </summary>
    public static ContentRisk Evaluate(
        string title, string description, decimal? price, Category category,
        bool hasImages, ModerationOptions options)
    {
        var text = $"{title}\n{description}";
        var signals = new List<string>();
        var score = 0;
        var stopWord = false;

        if (FindStopWord(text, options.StopWords) is { } word)
        {
            stopWord = true;
            score += options.StopWordPoints;
            signals.Add($"стоп-слово «{word}»");
        }

        if (Matches(ExternalLinkRegex(), text))
        {
            score += options.ExternalLinkPoints;
            signals.Add("ссылка или ник мессенджера в тексте");
        }

        if (Matches(PhoneLikeRegex(), text))
        {
            score += options.ContactInTextPoints;
            signals.Add("телефон в тексте");
        }

        if (price is { } p && p >= options.HighPriceThreshold)
        {
            score += options.HighPricePoints;
            signals.Add($"цена от {options.HighPriceThreshold:N0} руб.");
        }

        if (options.RiskyCategories.Contains(category))
        {
            score += options.RiskyCategoryPoints;
            signals.Add($"рисковая категория {category}");
        }

        if (!hasImages)
        {
            score += options.NoImagesPoints;
            signals.Add("нет фотографий");
        }

        return new ContentRisk(score, signals) { HasStopWord = stopWord };
    }

    private static string? FindStopWord(string text, string[] stopWords)
    {
        foreach (var word in stopWords)
        {
            if (string.IsNullOrWhiteSpace(word))
                continue;
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
                return word;
        }
        return null;
    }

    /// <summary>
    /// Срабатывание таймаута регулярки трактуем как совпадение: текст, который не удалось
    /// разобрать за 100 мс, — сам по себе повод показать объявление человеку.
    /// </summary>
    private static bool Matches(Regex regex, string text)
    {
        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }
}
