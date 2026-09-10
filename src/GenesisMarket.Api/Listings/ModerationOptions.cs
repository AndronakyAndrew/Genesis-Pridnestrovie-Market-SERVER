using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Listings;

/// <summary>
/// Веса и пороги политики модерации. Секция <c>Moderation</c>.
///
/// Решение принимается по одному числу: <c>score = доверие автора − риск контента</c>.
/// <list type="bullet">
/// <item><c>score >= AutoPublishScore</c> — сразу в каталог, модератор не участвует;</item>
/// <item><c>score >= PostReviewScore</c> — сразу в каталог + очередь на постмодерацию;</item>
/// <item>иначе — премодерация, до одобрения в каталоге объявления нет.</item>
/// </list>
/// Веса вынесены в конфиг намеренно: пороги придётся двигать по реальной статистике
/// нарушений, а не по догадкам на старте. Значения по умолчанию подобраны так, чтобы
/// новичок (0 одобренных, подтверждена только почта) шёл на премодерацию — как было
/// до появления скоринга.
/// </summary>
public sealed class ModerationOptions
{
    public const string Section = "Moderation";

    // ---- Пороги решения ----

    /// <summary>Начиная с этого score объявление публикуется без проверки вообще.</summary>
    public int AutoPublishScore { get; set; } = 60;

    /// <summary>
    /// Начиная с этого score объявление идёт в каталог, но с постмодерацией.
    /// При 40 аккаунт нулевого возраста не проходит даже сюда (почта 10 + телефон 25 = 35):
    /// первое объявление свежерегистрированного автора видит модератор ДО каталога.
    /// Снижать порог ниже суммы одних только подтверждений опасно: регистрация с SIM-картой
    /// открывала бы витрину в ту же минуту.
    /// </summary>
    public int PostReviewScore { get; set; } = 40;

    /// <summary>
    /// Выключатель постмодерации целиком. false — режим PostReview схлопывается
    /// в публикацию без проверки (очередь модератора не растёт). Аварийный тумблер
    /// на случай, если очередь перестают разбирать.
    /// </summary>
    public bool PostModerationEnabled { get; set; } = true;

    // ---- Доверие автора ----

    /// <summary>Очков за каждое одобренное объявление (<c>users.ApprovedListingsCount</c>).</summary>
    public int ApprovedListingPoints { get; set; } = 15;

    /// <summary>Сколько одобренных объявлений максимум идёт в зачёт.</summary>
    public int MaxApprovedListingsCounted { get; set; } = 5;

    /// <summary>
    /// Сколько одобренных объявлений нужно минимум, чтобы автопубликация вообще стала
    /// возможна. Жёсткий пол поверх score: отстоянный аккаунт с подтверждённым телефоном
    /// набирает проходной балл, ни разу ничего не опубликовав, — а «состарить» аккаунт
    /// и получить SIM дешевле, чем провести объявление через модератора.
    /// </summary>
    public int MinApprovedForAutoPublish { get; set; } = 1;

    /// <summary>Очков за каждый день возраста аккаунта.</summary>
    public int AccountAgeDayPoints { get; set; } = 1;

    /// <summary>Сколько дней возраста максимум идёт в зачёт.</summary>
    public int MaxAccountAgeDaysCounted { get; set; } = 30;

    public int EmailVerifiedPoints { get; set; } = 10;

    /// <summary>Телефон дороже почты: SMS-подтверждение труднее массово подделать.</summary>
    public int PhoneVerifiedPoints { get; set; } = 25;

    /// <summary>Бонус за хороший рейтинг (нужны и <see cref="MinReviewsForRatingBonus"/>, и <see cref="MinRatingForBonus"/>).</summary>
    public int GoodRatingPoints { get; set; } = 15;

    public int MinReviewsForRatingBonus { get; set; } = 3;

    public double MinRatingForBonus { get; set; } = 4.0;

    /// <summary>
    /// Потолок доверия. Без него ветеран площадки публиковал бы что угодно: риск
    /// контента переставал бы что-либо решать. Значение задаёт запас над
    /// <see cref="AutoPublishScore"/> — сколько очков риска максимально доверенный автор
    /// способен «съесть», не попав в очередь. При 85/60 запас равен 25, то есть одна
    /// ссылка наружу (<see cref="ExternalLinkPoints"/> = 30) уводит на проверку кого угодно.
    /// Поднимать этот потолок, не подняв заодно пороги, — значит незаметно отключить
    /// риск-оценку для старых аккаунтов.
    /// </summary>
    public int MaxAuthorTrust { get; set; } = 85;

    // ---- Жёсткие правила (сильнее любого score) ----

    /// <summary>
    /// Свежий отказ модератора (окно — <c>CatalogHygiene:RejectLookbackDays</c>)
    /// закрывает автопубликацию: всё уходит на премодерацию.
    /// </summary>
    public bool PreReviewAfterReject { get; set; } = true;

    /// <summary>Стоп-слово в тексте всегда уводит на премодерацию, каким бы ни был score.</summary>
    public bool PreReviewOnStopWord { get; set; } = true;

    // ---- Риск контента ----

    /// <summary>Ссылка наружу либо ник мессенджера в тексте.</summary>
    public int ExternalLinkPoints { get; set; } = 30;

    /// <summary>Телефон в тексте — обход раскрытия контактов площадки.</summary>
    public int ContactInTextPoints { get; set; } = 20;

    public int StopWordPoints { get; set; } = 50;

    public int HighPricePoints { get; set; } = 25;

    /// <summary>Цена (руб. ПМР), начиная с которой объявление считается дорогим.</summary>
    public decimal HighPriceThreshold { get; set; } = 100_000;

    public int RiskyCategoryPoints { get; set; } = 20;

    /// <summary>Категории с исторически высокой долей мошенничества.</summary>
    public Category[] RiskyCategories { get; set; } =
        [Category.RealEstate, Category.Transport, Category.Work, Category.Services];

    /// <summary>Объявление без единой фотографии — слабый, но реальный признак пустышки.</summary>
    public int NoImagesPoints { get; set; } = 10;

    /// <summary>
    /// Маркеры заведомо запрещённых товаров и услуг. Список — стартовый: расширяется
    /// по мере разбора реальных отказов, поэтому и лежит в конфиге, а не в коде.
    /// Сравнение регистронезависимое, по вхождению подстроки в нормализованный текст.
    /// </summary>
    public string[] StopWords { get; set; } =
    [
        "наркотик", "закладк", "мефедрон", "амфетамин",
        "оружие", "патрон", "глушител",
        "поддельн", "фальшив", "обнал",
        "диплом под ключ", "справк задним числом",
        "документы на заказ", "паспорт куплю"
    ];

    // ---- Очередь ----

    /// <summary>
    /// Потолок приоритета, который риск-оценка может присвоить объявлению в очереди.
    /// Заведомо ниже <c>Trust:AutoFlagPriority</c> (100): подтверждённые жалобы должны
    /// разбираться раньше подозрительной, но никем не пожаловавшейся публикации.
    /// </summary>
    public int MaxQueuePriority { get; set; } = 50;
}
