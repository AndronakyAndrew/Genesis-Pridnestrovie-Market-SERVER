using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Listings;

/// <summary>
/// Русские подписи справочников каталога (города, категории) для текста, видимого людям:
/// посты в Telegram-канал и т.п. Значения соответствуют справочникам из CLAUDE.md.
/// </summary>
public static class CatalogLabels
{
    public static string City(City city) => city switch
    {
        Domain.Enums.City.Tiraspol => "Тирасполь",
        Domain.Enums.City.Bendery => "Бендеры",
        Domain.Enums.City.Rybnitsa => "Рыбница",
        Domain.Enums.City.Dubossary => "Дубоссары",
        Domain.Enums.City.Slobodzea => "Слободзея",
        Domain.Enums.City.Grigoriopol => "Григориополь",
        Domain.Enums.City.Dnestrovsk => "Днестровск",
        _ => city.ToString()
    };

    public static string Category(Category category) => category switch
    {
        Domain.Enums.Category.RealEstate => "Недвижимость",
        Domain.Enums.Category.Transport => "Транспорт",
        Domain.Enums.Category.Electronics => "Электроника",
        Domain.Enums.Category.Home => "Дом и сад",
        Domain.Enums.Category.Fashion => "Одежда и обувь",
        Domain.Enums.Category.Kids => "Детские товары",
        Domain.Enums.Category.Work => "Работа",
        Domain.Enums.Category.Services => "Услуги",
        Domain.Enums.Category.Animals => "Животные",
        Domain.Enums.Category.Other => "Разное",
        _ => category.ToString()
    };
}
