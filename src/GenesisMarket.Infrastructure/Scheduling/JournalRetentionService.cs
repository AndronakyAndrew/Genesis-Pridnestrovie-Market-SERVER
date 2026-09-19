using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Infrastructure.Scheduling;

/// <summary>Что сделала уборка журналов за один прогон.</summary>
public sealed record JournalRetentionResult(int RevealsAnonymized, int ClicksRemoved);

public interface IJournalRetentionService
{
    Task<JournalRetentionResult> RunAsync(CancellationToken ct);
}

/// <summary>
/// Срок хранения псевдонимизированного IP в журналах.
///
/// С двумя таблицами обращаемся по-разному, и это не небрежность:
/// <list type="bullet">
/// <item><c>contact_reveals</c> — строку НЕ удаляем. На ней держатся счётчик
/// <c>contactRevealCount</c> в карточке и гейт отзывов (отзыв возможен только после
/// раскрытия контактов ЭТИМ пользователем, см. ReviewsController). Стираем ровно то,
/// что указывает на человека за пределами площадки, — <c>IpHash</c>;
/// <c>ViewerUserId</c> остаётся.</item>
/// <item><c>link_clicks</c> — никем не агрегируется и растёт быстрее всех, поэтому
/// удаляется целиком.</item>
/// </list>
/// Идемпотентен: повторный прогон в тот же день не находит строк.
/// </summary>
public sealed class JournalRetentionService(
    AppDbContext db,
    IOptions<JournalRetentionOptions> options,
    ILogger<JournalRetentionService> logger) : IJournalRetentionService
{
    /// <summary>
    /// Значение <c>IpHash</c> после истечения срока хранения. Колонка обязательная,
    /// поэтому не NULL, а явный маркер: по нему видно, что адрес снят по сроку,
    /// а не потерян.
    /// </summary>
    public const string ExpiredIpHash = "expired";

    private readonly JournalRetentionOptions _o = options.Value;

    public async Task<JournalRetentionResult> RunAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var revealCutoff = now.AddDays(-_o.ContactRevealIpDays);
        var anonymized = await db.ContactReveals
            .Where(r => r.CreatedAt < revealCutoff && r.IpHash != ExpiredIpHash)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.IpHash, ExpiredIpHash), ct);

        var clickCutoff = now.AddDays(-_o.LinkClickDays);
        var removed = await db.LinkClicks
            .Where(c => c.CreatedAt < clickCutoff)
            .ExecuteDeleteAsync(ct);

        if (anonymized > 0 || removed > 0)
            logger.LogInformation(
                "Уборка журналов: обезличено раскрытий — {Anonymized}, удалено переходов — {Removed}.",
                anonymized, removed);

        return new JournalRetentionResult(anonymized, removed);
    }
}
