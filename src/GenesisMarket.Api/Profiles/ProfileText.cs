using System.Text.RegularExpressions;

namespace GenesisMarket.Api.Profiles;

/// <summary>
/// Нормализация свободного текста профиля. Единственная точка приведения
/// «О себе» к виду, пригодному для хранения, — по образцу
/// <see cref="Auth.PhoneNumber"/> для телефона.
///
/// HTML здесь намеренно НЕ вырезается и не экранируется: в проекте экранирование
/// делается на границе вывода (<c>HtmlEncoder</c> в письмах и Telegram-постах,
/// см. Feedback/ResendEmailService и Outbox/NotificationChannels), а в JSON-ответы
/// текст уходит как данные. Хранить нужно ровно то, что ввёл пользователь.
/// </summary>
public static partial class ProfileText
{
    /// <summary>Максимум символов «О себе». Совпадает с HasMaxLength(300) и varchar(300).</summary>
    public const int MaxDescriptionLength = 300;

    /// <summary>
    /// Приводит «О себе» к каноническому виду:
    /// переводы строк → LF, 3+ подряд идущих переноса → два (максимум одна
    /// пустая строка), пробелы по краям сняты, пустой результат → null,
    /// длина обрезана до <see cref="MaxDescriptionLength"/>.
    /// Обрезка — последним шагом, уже после схлопывания и trim: иначе крайние
    /// пробелы считались бы значимыми символами и съедали лимит.
    /// </summary>
    public static string? NormalizeDescription(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        // CRLF/CR → LF: иначе счёт переносов зависел бы от ОС клиента.
        var text = input.Replace("\r\n", "\n").Replace('\r', '\n');

        // Пустые строки схлопываем, считая строку из одних пробелов пустой:
        // в интерфейсе она неотличима от пустой.
        text = BlankLineRuns().Replace(text, "\n\n");

        text = text.Trim();
        if (text.Length == 0)
            return null;

        if (text.Length <= MaxDescriptionLength)
            return text;

        // Не разрезаем суррогатную пару (эмодзи) пополам — иначе в БД уедет
        // «половина символа». Хвостовые пробелы после обрезки снимаем.
        var cut = MaxDescriptionLength;
        if (char.IsHighSurrogate(text[cut - 1]))
            cut--;

        return text[..cut].TrimEnd();
    }

    /// <summary>Три и более перевода строки подряд (строки из пробелов/табов — тоже пустые).</summary>
    [GeneratedRegex(@"(?:[ \t]*\n){3,}", RegexOptions.CultureInvariant)]
    private static partial Regex BlankLineRuns();
}
