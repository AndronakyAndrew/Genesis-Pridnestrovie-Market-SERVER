using GenesisMarket.Domain.Common;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Объявление каталога. Цена — в рублях ПМР, целое число (numeric(12,0)).
/// Удаление — мягкое (<see cref="DeletedAt"/> + глобальный query filter).
/// </summary>
public class Listing : BaseEntity, IOwnedResource
{
    /// <summary>Заголовок. Длина 5..120 гарантируется CHECK-констрейнтом БД.</summary>
    public required string Title { get; set; }

    /// <summary>
    /// ЧПУ-адрес: транслитерация Title + короткий хеш Id. Уникален на уровне БД,
    /// генерируется сервером и не меняется при редактировании Title.
    /// </summary>
    public required string Slug { get; set; }

    /// <summary>Описание, до 5000 символов (CHECK на уровне БД).</summary>
    public required string Description { get; set; }

    /// <summary>
    /// Цена в рублях ПМР. null допустим только при <see cref="PriceType.Negotiable"/>.
    /// Согласованность с <see cref="PriceType"/> проверяется CHECK-констрейнтом.
    /// </summary>
    public decimal? Price { get; set; }

    public PriceType PriceType { get; set; } = PriceType.Fixed;

    public Category Category { get; set; }

    /// <summary>FK на справочник подкатегорий. Пара (Category, Subcategory) валидируется на сервере.</summary>
    public int SubcategoryId { get; set; }
    public Subcategory? Subcategory { get; set; }

    public City City { get; set; }

    /// <summary>Район/микрорайон. До 100 символов (CHECK на уровне БД).</summary>
    public string? District { get; set; }

    public Condition Condition { get; set; } = Condition.NotApplicable;

    /// <summary>
    /// Статус объявления. Для жизненного цикла каталога (публикация, поднятие,
    /// продажа, архивация, восстановление) меняется ТОЛЬКО через доменные методы
    /// этого класса (см. <see cref="Publish"/>, <see cref="Bump"/>, <see cref="Archive"/>,
    /// <see cref="MarkSold"/>, <see cref="ReactivateFromSold"/>, <see cref="RestoreFromArchive"/>),
    /// а не присваиванием в контроллере.
    /// </summary>
    public ListingStatus Status { get; set; } = ListingStatus.Draft;

    /// <summary>
    /// Счётчик просмотров. Извне не редактируется: приватный сеттер,
    /// увеличивается только через <see cref="RegisterView"/> отдельной операцией
    /// (атомарный UPDATE ... SET "ViewsCount" = "ViewsCount" + 1 на уровне запроса).
    /// </summary>
    public int ViewsCount { get; private set; }

    /// <summary>
    /// Денормализованный счётчик добавлений в избранное. Извне не редактируется:
    /// поддерживается триггером БД (<c>favorites_count</c>) при вставке/удалении
    /// строк в <see cref="Favorite"/> — не COUNT на каждый запрос каталога.
    /// </summary>
    public int FavoritesCount { get; private set; }

    /// <summary>
    /// Приоритет в очереди модерации. Обычно 0; поднимается автоматикой жалоб
    /// (N независимых Fraud/Prohibited на объявление) — тогда объявление уходит
    /// в PendingReview и в начало очереди модерации. Извне не редактируется.
    /// </summary>
    public int ModerationPriority { get; private set; }

    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>
    /// Момент последнего поднятия («bump»). По нему (с откатом на <see cref="PublishedAt"/>)
    /// строится сортировка каталога по умолчанию и отсчёт срока автоархивации.
    /// Меняется только доменными методами (<see cref="Bump"/>, <see cref="Publish"/>,
    /// <see cref="RestoreFromArchive"/>).
    /// </summary>
    public DateTimeOffset? BumpedAt { get; private set; }

    /// <summary>Момент ухода в архив. Нужен для окна восстановления (90 дней).</summary>
    public DateTimeOffset? ArchivedAt { get; private set; }

    /// <summary>Момент отметки «продано». Нужен для окна обратного перехода Sold → Active (7 дней).</summary>
    public DateTimeOffset? SoldAt { get; private set; }

    /// <summary>
    /// Момент, когда автору отправлено предупреждение о скорой архивации. Заполнено ⇒
    /// повторно не уведомляем; сбрасывается при поднятии/восстановлении. Обеспечивает
    /// идемпотентность джоба автоархивации.
    /// </summary>
    public DateTimeOffset? ArchiveWarningAt { get; private set; }

    /// <summary>Метка мягкого удаления. Заполнена ⇒ объявление скрыто query-фильтром.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    // Владелец. Проверяется на сервере в каждом мутирующем эндпоинте.
    public Guid OwnerId { get; set; }
    public User? Owner { get; set; }

    /// <summary>
    /// Идентификатор канала/чата, куда объявление было опубликовано в Telegram
    /// (маршрутизация категория → канал; может отличаться от общего канала). Заполнен
    /// вместе с <see cref="TelegramMessageId"/> — нужен, чтобы позже отредактировать
    /// именно тот пост (пометки «Продано»/«Снято»). null ⇒ в канал ещё не постили.
    /// </summary>
    public string? TelegramChatId { get; set; }

    /// <summary>
    /// message_id опубликованного поста в Telegram (см. <see cref="TelegramChatId"/>).
    /// Заполняется обработчиком outbox после успешной отправки. null ⇒ поста нет
    /// (не публиковали, отправка не удалась или пост был удалён вручную).
    /// </summary>
    public long? TelegramMessageId { get; set; }

    public ICollection<ListingImage> Images { get; set; } = new List<ListingImage>();

    /// <summary>
    /// Привязать объявление к опубликованному посту канала. Единственная точка записи
    /// координат поста (chatId + messageId) — вызывается обработчиком outbox после
    /// успешного sendPhoto/sendMessage.
    /// </summary>
    public void AttachChannelPost(string chatId, long messageId)
    {
        TelegramChatId = chatId;
        TelegramMessageId = messageId;
    }

    /// <summary>Единственная точка изменения счётчика просмотров в доменной модели.</summary>
    public void RegisterView() => ViewsCount++;

    // ---- Переходы жизненного цикла (единственный способ менять статус/метки каталога) ----

    /// <summary>
    /// Публикация черновика: Draft → Active | PendingReview. Фиксирует <see cref="PublishedAt"/>;
    /// для сразу активного объявления задаёт и <see cref="BumpedAt"/> (позиция в каталоге).
    /// Итоговый статус (нужна ли премодерация) решает вызывающий и передаёт в <paramref name="target"/>.
    /// </summary>
    public void Publish(ListingStatus target, DateTimeOffset now)
    {
        if (Status != ListingStatus.Draft)
            throw new InvalidOperationException($"Опубликовать можно только черновик (текущий статус: {Status}).");
        if (target is not (ListingStatus.Active or ListingStatus.PendingReview))
            throw new ArgumentException("Публикация допускает переход только в Active или PendingReview.", nameof(target));

        Status = target;
        PublishedAt = now;
        if (target == ListingStatus.Active)
            BumpedAt = now;
        ArchiveWarningAt = null;
        UpdatedAt = now;
    }

    /// <summary>
    /// Поднятие: обновляет <see cref="BumpedAt"/> у активного объявления и сбрасывает
    /// отметку предупреждения об архивации. Соблюдение лимита частоты — забота вызывающего.
    /// </summary>
    public void Bump(DateTimeOffset now)
    {
        if (Status != ListingStatus.Active)
            throw new InvalidOperationException($"Поднять можно только активное объявление (текущий статус: {Status}).");

        BumpedAt = now;
        ArchiveWarningAt = null;
        UpdatedAt = now;
    }

    /// <summary>
    /// Уход в архив: любой статус → Archived (кроме уже архивного — идемпотентно).
    /// Фиксирует <see cref="ArchivedAt"/> для окна восстановления.
    /// </summary>
    public void Archive(DateTimeOffset now)
    {
        if (Status == ListingStatus.Archived)
            return; // повторный вызов ничего не меняет

        Status = ListingStatus.Archived;
        ArchivedAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Отметка, что автору отправлено предупреждение о скорой архивации.
    /// Идемпотентность джоба: после вызова объявление в выборку предупреждений не попадает.
    /// </summary>
    public void MarkArchiveWarned(DateTimeOffset now)
    {
        ArchiveWarningAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Отметить проданным: Active → Sold. Объявление остаётся доступным по прямой ссылке,
    /// но уходит из каталога. Фиксирует <see cref="SoldAt"/> для окна обратного перехода.
    /// </summary>
    public void MarkSold(DateTimeOffset now)
    {
        if (Status != ListingStatus.Active)
            throw new InvalidOperationException($"Отметить проданным можно только активное объявление (текущий статус: {Status}).");

        Status = ListingStatus.Sold;
        SoldAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Обратный переход из продажи: Sold → Active. Позицию в каталоге (<see cref="BumpedAt"/>)
    /// намеренно НЕ обновляем, чтобы «продал/вернул» не использовали для поднятия.
    /// Соблюдение окна (7 дней с <see cref="SoldAt"/>) — забота вызывающего.
    /// </summary>
    public void ReactivateFromSold(DateTimeOffset now)
    {
        if (Status != ListingStatus.Sold)
            throw new InvalidOperationException($"Вернуть в продажу можно только проданное объявление (текущий статус: {Status}).");

        Status = ListingStatus.Active;
        SoldAt = null;
        UpdatedAt = now;
    }

    /// <summary>
    /// Восстановление из архива: Archived → Active | PendingReview. При возврате в каталог
    /// объявление получает свежий <see cref="BumpedAt"/> (обнуляет отсчёт архивации).
    /// Нужна ли повторная премодерация (<paramref name="requireReview"/>) и соблюдение окна
    /// (90 дней с <see cref="ArchivedAt"/>) решает вызывающий.
    /// </summary>
    public void RestoreFromArchive(bool requireReview, DateTimeOffset now)
    {
        if (Status != ListingStatus.Archived)
            throw new InvalidOperationException($"Восстановить можно только архивное объявление (текущий статус: {Status}).");

        Status = requireReview ? ListingStatus.PendingReview : ListingStatus.Active;
        ArchivedAt = null;
        if (Status == ListingStatus.Active)
        {
            BumpedAt = now;
            ArchiveWarningAt = null;
        }
        UpdatedAt = now;
    }
}
