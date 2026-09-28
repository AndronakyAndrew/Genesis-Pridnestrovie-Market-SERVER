using GenesisMarket.Domain.Common;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Domain.Entities;

/// <summary>Статус приглашения продавца. В БД — строкой.</summary>
public enum InvitationStatus
{
    /// <summary>«Готовится»: сотрудник набирает черновики, ссылки ещё нет.</summary>
    Draft,

    /// <summary>«Ожидает принятия»: ссылка выдана, идёт срок действия.</summary>
    Pending,

    /// <summary>«Принято»: черновики перешли в аккаунт владельца. Конечный статус.</summary>
    Accepted,

    /// <summary>«Истекло»: срок ссылки вышел. Можно выпустить новую.</summary>
    Expired,

    /// <summary>«Отозвано» сотрудником. Конечный статус.</summary>
    Revoked
}

/// <summary>
/// Приглашение продавца: сотрудник готовит черновики товаров магазина, владелец
/// магазина регистрируется по одноразовой ссылке и публикует их сам.
///
/// Черновики принадлежат приглашению, а не пользователю: до принятия это отдельные
/// таблицы (<see cref="InvitationDraft"/>), в listings их нет — поэтому ни каталог,
/// ни поиск, ни sitemap, ни публичная выдача фото их не видят в принципе.
///
/// Статус меняется только через методы ниже; на сервере переходы дополнительно
/// защищены условным UPDATE (гонка «принять» против «отозвать»/«принять»).
/// </summary>
public class SellerInvitation : BaseEntity
{
    // ---- Шаг 1: магазин ----

    public required string ShopName { get; set; }

    /// <summary>Код города (FK на <c>cities.Code</c>).</summary>
    public required string City { get; set; }

    /// <summary>Основная категория магазина (FK на <c>categories.Code</c>).</summary>
    public required string Category { get; set; }

    /// <summary>Внутренняя заметка — видна только сотрудникам, владельцу не отдаётся никогда.</summary>
    public string? InternalNote { get; set; }

    // ---- Жизненный цикл ----

    public InvitationStatus Status { get; private set; } = InvitationStatus.Draft;

    /// <summary>Когда статус менялся последний раз — от него считаются сроки очистки.</summary>
    public DateTimeOffset StatusChangedAt { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>SHA-256 токена ссылки. Сам токен в БД не хранится; null — ссылки нет.</summary>
    public byte[]? TokenHash { get; private set; }

    public DateTimeOffset? LinkIssuedAt { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>Сколько раз ссылка выпускалась (первый выпуск — 1).</summary>
    public int LinkVersion { get; private set; }

    /// <summary>Сотрудник, подготовивший приглашение. Без FK: переживает удаление аккаунта.</summary>
    public Guid CreatedByUserId { get; set; }

    public Guid? AcceptedByUserId { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public ICollection<InvitationDraft> Drafts { get; set; } = new List<InvitationDraft>();

    /// <summary>Черновики редактируются, пока приглашение не принято и не закрыто.</summary>
    public bool IsEditable => Status is InvitationStatus.Draft or InvitationStatus.Pending or InvitationStatus.Expired;

    /// <summary>Ссылка действует: выдана, не использована, срок не вышел.</summary>
    public bool IsLinkActive(DateTimeOffset now) =>
        Status == InvitationStatus.Pending && ExpiresAt is { } exp && exp > now;

    /// <summary>
    /// Выпустить ссылку (первый раз — Draft → Pending) или перевыпустить взамен прежней
    /// (Pending/Expired → Pending): старый хеш заменяется, старая ссылка перестаёт работать.
    /// </summary>
    public void IssueLink(byte[] tokenHash, DateTimeOffset now, TimeSpan lifetime)
    {
        if (Status is not (InvitationStatus.Draft or InvitationStatus.Pending or InvitationStatus.Expired))
            throw new InvalidOperationException($"Ссылку нельзя выпустить в статусе {Status}.");

        TokenHash = tokenHash;
        LinkIssuedAt = now;
        ExpiresAt = now + lifetime;
        LinkVersion++;
        SetStatus(InvitationStatus.Pending, now);
    }

    /// <summary>Отозвать: из «Готовится» (отмена) или «Ожидает принятия». Ссылка гасится.</summary>
    public void Revoke(DateTimeOffset now)
    {
        if (Status is not (InvitationStatus.Draft or InvitationStatus.Pending or InvitationStatus.Expired))
            throw new InvalidOperationException($"Приглашение нельзя отозвать в статусе {Status}.");

        TokenHash = null;
        SetStatus(InvitationStatus.Revoked, now);
    }

    /// <summary>Срок вышел: Pending → Expired. Хеш остаётся — по нему отличаем «истекла» от «нет такой».</summary>
    public void Expire(DateTimeOffset now)
    {
        if (Status != InvitationStatus.Pending)
            throw new InvalidOperationException($"Истечь может только ожидающее приглашение, а не {Status}.");
        SetStatus(InvitationStatus.Expired, now);
    }

    /// <summary>
    /// Отметить принятым в памяти (после того, как условный UPDATE в БД уже выиграл гонку).
    /// Хеш гасится: ссылка одноразовая.
    /// </summary>
    public void MarkAccepted(Guid ownerId, DateTimeOffset now)
    {
        AcceptedByUserId = ownerId;
        AcceptedAt = now;
        TokenHash = null;
        SetStatus(InvitationStatus.Accepted, now);
    }

    private void SetStatus(InvitationStatus status, DateTimeOffset now)
    {
        Status = status;
        StatusChangedAt = now;
        UpdatedAt = now;
    }
}

/// <summary>
/// Черновик товара внутри приглашения. Поля — те же, что у объявления, и проверяются
/// теми же правилами: при принятии черновик становится <see cref="Listing"/> в статусе
/// Draft без дополнительного ввода.
/// </summary>
public class InvitationDraft : BaseEntity
{
    public Guid InvitationId { get; set; }
    public SellerInvitation? Invitation { get; set; }

    public required string Title { get; set; }
    public required string Description { get; set; }
    public PriceType PriceType { get; set; } = PriceType.Fixed;

    /// <summary>Рубли ПМР; null — только для договорной цены (как у объявлений).</summary>
    public decimal? Price { get; set; }

    public required string Category { get; set; }
    public int SubcategoryId { get; set; }
    public Condition Condition { get; set; } = Condition.NotApplicable;

    public int SortOrder { get; set; }

    /// <summary>Сотрудник, последним менявший черновик (для журнала «кто подготовил»).</summary>
    public Guid UpdatedByUserId { get; set; }

    /// <summary>Объявление, в которое превратился черновик при принятии (null — ещё не принят).</summary>
    public Guid? ListingId { get; set; }

    public ICollection<InvitationDraftPhoto> Photos { get; set; } = new List<InvitationDraftPhoto>();
}

/// <summary>
/// Фото черновика. Лежит в MinIO под префиксом <c>invitations/{invitationId}/</c>, который
/// публичная выдача (<c>/api/images/listings/...</c>) не обслуживает: до принятия фото
/// видны только сотруднику и по действующей ссылке приглашения.
/// </summary>
public class InvitationDraftPhoto
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid DraftId { get; set; }
    public InvitationDraft? Draft { get; set; }

    public required string ObjectKey { get; set; }
    public required string ThumbKey { get; set; }
    public int SortOrder { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Согласие владельца на публикацию товара и своих контактных данных. Строка на
/// объявление: публикация объявления из приглашения без согласия текущей версии
/// отклоняется сервером. Только на добавление.
/// </summary>
public class PublicationConsent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }

    public Guid ListingId { get; set; }

    /// <summary>Версия текста согласия (конфиг <c>Invitations:ConsentVersion</c>).</summary>
    public required string Version { get; set; }

    public DateTimeOffset AcceptedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>HMAC адреса, с которого дано согласие.</summary>
    public string? IpHash { get; set; }
}
