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

    /// <summary>Метка мягкого удаления. Заполнена ⇒ объявление скрыто query-фильтром.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    // Владелец. Проверяется на сервере в каждом мутирующем эндпоинте.
    public Guid OwnerId { get; set; }
    public User? Owner { get; set; }

    public ICollection<ListingImage> Images { get; set; } = new List<ListingImage>();

    /// <summary>Единственная точка изменения счётчика просмотров в доменной модели.</summary>
    public void RegisterView() => ViewsCount++;
}
