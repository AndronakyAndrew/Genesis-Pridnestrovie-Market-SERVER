namespace GenesisMarket.Api.Accounts;

public sealed record NotificationGroupDefinition(string Key, string Description, IReadOnlyList<string> Types, bool CanDisable);

/// <summary>Только серверный каталог групп аккаунтных уведомлений.</summary>
public static class NotificationGroups
{
    public const string Security = "security";
    public const string Activity = "activity";
    public const string Reviews = "reviews";
    public const string SavedSearches = "saved_searches";
    public const string Feedback = "feedback";

    public static readonly IReadOnlyList<NotificationGroupDefinition> All =
    [
        new(Security, "Безопасность аккаунта",
            ["Коды подтверждения и входа по email", "Восстановление пароля", "Смена email", "Подтверждение удаления аккаунта"],
            CanDisable: false),
        new(Activity, "Активность объявлений и магазина",
            ["Объявление одобрено", "Объявление отклонено", "Объявление возвращено на доработку", "Скоро автоархивация", "Приглашение магазина принято"],
            CanDisable: true),
        new(Reviews, "Отзывы",
            ["Новый отзыв о продавце"],
            CanDisable: true),
        new(SavedSearches, "Сохранённые поиски",
            ["Новые объявления по сохранённому поиску"],
            CanDisable: true),
        new(Feedback, "Обратная связь",
            ["Подтверждение получения обращения, если контакт совпадает с email аккаунта"],
            CanDisable: true)
    ];

    public static bool IsKnown(string key) => All.Any(x => x.Key == key);
    public static bool CanDisable(string key) => All.First(x => x.Key == key).CanDisable;
}
