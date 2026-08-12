using System.Text.RegularExpressions;

namespace GenesisMarket.Api.Outbox;

// ---- Полезные нагрузки сообщений. Только идентификаторы и минимум для рендера. ----
// Персональные данные (email/телефон/имя) в payload НЕ кладём: адресата и контент
// обработчик достаёт из БД по id на момент отправки.

/// <summary>Payload <see cref="Domain.Entities.OutboxMessage.ListingApproved"/>.</summary>
public sealed record ListingApprovedPayload(Guid ListingId);

/// <summary>Payload <see cref="Domain.Entities.OutboxMessage.ListingRejected"/>.</summary>
public sealed record ListingRejectedPayload(Guid ListingId, string Reason, string? Comment);

/// <summary>Payload <see cref="Domain.Entities.OutboxMessage.ListingExpiringSoon"/>.</summary>
public sealed record ListingExpiringSoonPayload(Guid ListingId, DateTimeOffset ArchiveAt);

/// <summary>Payload <see cref="Domain.Entities.OutboxMessage.NewReview"/>.</summary>
public sealed record NewReviewPayload(Guid ReviewId);

/// <summary>Payload <see cref="Domain.Entities.OutboxMessage.ListingPublished"/>.</summary>
public sealed record ListingPublishedPayload(Guid ListingId);

/// <summary>Payload <see cref="Domain.Entities.OutboxMessage.SavedSearchMatch"/>.</summary>
public sealed record SavedSearchMatchPayload(Guid SavedSearchId, IReadOnlyList<Guid> ListingIds);

/// <summary>
/// Ошибка доставки, которую бессмысленно повторять (некорректный payload, удалённый
/// адресат, отсутствующий ресурс, канал не сконфигурирован). Диспетчер сразу переводит
/// сообщение в <see cref="Domain.Enums.OutboxStatus.Failed"/>, не тратя попытки.
/// </summary>
public sealed class OutboxPermanentException(string message) : Exception(message);

/// <summary>
/// Вычищает персональные данные из строк, попадающих в лог и в поле Error сообщения:
/// адреса почты и телефоноподобные последовательности. Требование: логи outbox не должны
/// содержать email и телефоны (сообщение об ошибке SMTP часто включает адрес получателя).
/// </summary>
public static partial class PiiScrubber
{
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        text = EmailRegex().Replace(text, "[email]");
        text = PhoneRegex().Replace(text, "[phone]");
        return text;
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    // 7+ цифр, допускающие ведущий + и разделители (пробел/дефис/скобки) — телефон.
    [GeneratedRegex(@"\+?\d[\d\s\-()]{6,}\d", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();
}
