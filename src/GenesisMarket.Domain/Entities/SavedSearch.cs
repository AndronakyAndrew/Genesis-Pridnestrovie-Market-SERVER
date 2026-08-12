using GenesisMarket.Domain.Common;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Сохранённый поиск пользователя — главный механизм возврата на площадку: набор
/// параметров каталога, по которым фоновый джоб находит новые объявления и уведомляет
/// автора. Критерии хранятся в <see cref="QueryJson"/> (jsonb) и при каждом прогоне
/// заново валидируются теми же правилами, что и живой каталог: содержимому jsonb
/// не доверяем.
/// </summary>
public class SavedSearch : BaseEntity
{
    /// <summary>Владелец поиска. Уведомления адресуются ему.</summary>
    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Человекочитаемое имя поиска (для списка «мои поиски»). До 100 символов.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// Критерии поиска в JSON: ровно те же параметры, что принимает <c>GET /api/listings</c>
    /// (q, category, subcategory, cities, priceFrom, priceTo, condition, priceType).
    /// Столбец — jsonb. Валидность не гарантируется хранилищем и проверяется при каждом прогоне.
    /// </summary>
    public required string QueryJson { get; set; }

    /// <summary>
    /// Курсор «до какого объявления уже уведомляли»: Id последнего разосланного объявления.
    /// Позиция в потоке считается по паре (PublishedAt, Id) этого объявления — по курсору,
    /// а не по времени: объявления с одинаковым timestamp не теряются и не дублируются.
    /// null ⇒ поиск ещё ни разу не привязан к позиции (нового ничего не рассылалось).
    /// </summary>
    public Guid? LastNotifiedListingId { get; set; }

    /// <summary>Момент последнего прогона джоба по этому поиску (для наблюдаемости). </summary>
    public DateTimeOffset LastRunAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Активен ли поиск. Неактивные джоб не обрабатывает.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Каким каналом слать уведомления. <see cref="SavedSearchNotifyChannel.None"/> — не слать.</summary>
    public SavedSearchNotifyChannel NotifyChannel { get; set; } = SavedSearchNotifyChannel.Email;

    /// <summary>
    /// Момент последнего отправленного уведомления. Гарантирует «не чаще одного письма в час»:
    /// джоб пропускает поиск, если с <see cref="NotifiedAt"/> прошло меньше часа. null ⇒ ещё не слали.
    /// </summary>
    public DateTimeOffset? NotifiedAt { get; set; }
}
