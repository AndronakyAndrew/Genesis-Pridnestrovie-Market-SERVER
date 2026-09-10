using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Scheduling;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Listings;

/// <summary>
/// Решение политики: как публиковать объявление и почему. <see cref="Reason"/> уходит
/// в лог и в журнал модерации — без него разбирать «почему это ушло на премодерацию»
/// невозможно.
/// </summary>
public sealed record PublishDecision(
    PublishMode Mode,
    int Score,
    int Priority,
    string Reason)
{
    /// <summary>Публикация без участия модератора.</summary>
    public bool IsAuto => Mode == PublishMode.Auto;
}

/// <summary>
/// Решает, нужна ли объявлению проверка человеком и какая: премодерация (в каталог
/// не пускаем), постмодерация (пускаем, но покажем модератору) или ничего.
///
/// Решение = доверие автора − риск контента, пороги и веса — в <see cref="ModerationOptions"/>.
/// Поверх score стоят два жёстких правила: свежий отказ модератора и стоп-слово.
/// </summary>
public interface IListingModerationPolicy
{
    /// <summary>
    /// Решение для публикации/восстановления объявления. <paramref name="hasImages"/>
    /// передаётся отдельно: у только что созданного объявления фотографий ещё нет
    /// физически, и штрафовать за это нельзя.
    /// </summary>
    PublishDecision Resolve(
        User author, string title, string description, decimal? price, Category category, bool hasImages);
}

public sealed class ListingModerationPolicy(
    IOptions<ModerationOptions> options,
    IOptions<CatalogHygieneOptions> hygiene,
    ILogger<ListingModerationPolicy> logger) : IListingModerationPolicy
{
    private readonly ModerationOptions _o = options.Value;

    /// <summary>
    /// Окно «свежего» отказа. Осознанно переиспользуем настройку гигиены каталога:
    /// то же окно уже управляет повторной проверкой при восстановлении из архива,
    /// и разъезжаться этим двум срокам незачем.
    /// </summary>
    private int RejectLookbackDays => hygiene.Value.RejectLookbackDays;

    public PublishDecision Resolve(
        User author, string title, string description, decimal? price, Category category, bool hasImages)
    {
        var trust = AuthorTrust(author, out var trustSignals);
        var risk = ListingContentRisk.Evaluate(title, description, price, category, hasImages, _o);
        var score = trust - risk.Score;
        var priority = Math.Clamp(risk.Score, 0, _o.MaxQueuePriority);

        var decision = Decide(author, score, risk, priority, trust, trustSignals);

        logger.LogInformation(
            "Модерация публикации: автор {UserId}, доверие {Trust}, риск {Risk}, score {Score} ⇒ {Mode} ({Reason})",
            author.Id, trust, risk.Score, score, decision.Mode, decision.Reason);

        return decision;
    }

    private PublishDecision Decide(
        User author, int score, ContentRisk risk, int priority, int trust, string trustSignals)
    {
        // ---- Жёсткие правила: сильнее любого накопленного доверия ----

        if (_o.PreReviewOnStopWord && risk.HasStopWord)
            return new PublishDecision(PublishMode.PreReview, score, _o.MaxQueuePriority,
                $"стоп-слово в тексте; {Describe(risk)}");

        if (_o.PreReviewAfterReject && HasRecentReject(author, out var rejectedAt))
            return new PublishDecision(PublishMode.PreReview, score, priority,
                $"отказ модератора {rejectedAt:yyyy-MM-dd}, окно {RejectLookbackDays} дн.");

        // ---- Score ----

        var context = $"доверие {trust} ({trustSignals}), {Describe(risk)}";

        if (score >= _o.AutoPublishScore)
        {
            // Пол по одобренным: балл может набраться и без единой публикации.
            if (author.ApprovedListingsCount >= _o.MinApprovedForAutoPublish)
                return new PublishDecision(PublishMode.Auto, score, 0,
                    $"{context} — проверка не нужна");

            var shortfall = $"{context} — балла хватает, но одобренных объявлений " +
                            $"{author.ApprovedListingsCount} из {_o.MinApprovedForAutoPublish}";

            // Пол по одобренным не должен растворяться вместе с постмодерацией: если
            // очередь постмодерации выключена, первое объявление автора идёт к человеку
            // ДО каталога, а не мимо проверки вовсе.
            return _o.PostModerationEnabled
                ? new PublishDecision(PublishMode.PostReview, score, priority, shortfall)
                : new PublishDecision(PublishMode.PreReview, score, priority,
                    $"{shortfall}; постмодерация выключена — премодерация");
        }

        if (score >= _o.PostReviewScore)
            return PostReview(score, priority, $"{context} — постмодерация");

        return new PublishDecision(PublishMode.PreReview, score, priority,
            $"{context} — премодерация");
    }

    /// <summary>
    /// Постмодерация, если она включена; иначе объявление идёт в каталог без проверки —
    /// выключенный тумблер означает «очередь не растим», а не «блокируем публикации».
    /// </summary>
    private PublishDecision PostReview(int score, int priority, string reason) =>
        _o.PostModerationEnabled
            ? new PublishDecision(PublishMode.PostReview, score, priority, reason)
            : new PublishDecision(PublishMode.Auto, score, 0,
                $"{reason}; постмодерация выключена (Moderation:PostModerationEnabled)");

    /// <summary>
    /// Доверие автора. Все входы — денормализованные поля строки users, которую
    /// вызывающий уже загрузил: политика в БД не ходит вообще.
    /// </summary>
    private int AuthorTrust(User author, out string signals)
    {
        var parts = new List<string>();
        var trust = 0;

        var approved = Math.Min(author.ApprovedListingsCount, _o.MaxApprovedListingsCounted);
        if (approved > 0)
        {
            trust += approved * _o.ApprovedListingPoints;
            parts.Add($"одобрено {author.ApprovedListingsCount}");
        }

        var ageDays = (int)Math.Min(
            (DateTimeOffset.UtcNow - author.CreatedAt).TotalDays, _o.MaxAccountAgeDaysCounted);
        if (ageDays > 0)
        {
            trust += ageDays * _o.AccountAgeDayPoints;
            parts.Add($"возраст {ageDays} дн.");
        }

        if (author.EmailVerified)
        {
            trust += _o.EmailVerifiedPoints;
            parts.Add("почта подтверждена");
        }

        if (author.PhoneVerified)
        {
            trust += _o.PhoneVerifiedPoints;
            parts.Add("телефон подтверждён");
        }

        if (author.ReviewsCount >= _o.MinReviewsForRatingBonus &&
            author.AverageRating >= _o.MinRatingForBonus)
        {
            trust += _o.GoodRatingPoints;
            parts.Add($"рейтинг {author.AverageRating:0.0}");
        }

        signals = parts.Count > 0 ? string.Join(", ", parts) : "сигналов нет";
        return Math.Min(trust, _o.MaxAuthorTrust);
    }

    /// <summary>
    /// Получал ли автор отказ модератора за окно <c>CatalogHygiene:RejectLookbackDays</c>.
    /// Окно то же, что у восстановления из архива, — намеренно один источник правды.
    /// </summary>
    private bool HasRecentReject(User author, out DateTimeOffset rejectedAt)
    {
        rejectedAt = author.LastRejectedAt ?? default;
        if (author.LastRejectedAt is not { } last)
            return false;

        return DateTimeOffset.UtcNow - last < TimeSpan.FromDays(RejectLookbackDays);
    }

    private static string Describe(ContentRisk risk) =>
        risk.Signals.Count > 0 ? $"риск {risk.Score} ({string.Join(", ", risk.Signals)})" : "риска не найдено";
}
