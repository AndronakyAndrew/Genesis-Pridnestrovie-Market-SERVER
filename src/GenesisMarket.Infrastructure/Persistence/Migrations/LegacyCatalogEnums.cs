namespace GenesisMarket.Infrastructure.Persistence.Migrations;

// Замороженные копии enum-ов City и Category — только для истории миграций.
//
// До миграции CatalogReferencesToTables города и категории были native enum-типами
// PostgreSQL, и старые миграции (и их Designer-снимки) ссылаются на CLR-типы
// City/Category. Теперь это справочники в БД, а в GenesisMarket.Domain.Enums под
// этими именами — константы кодов сида. Объявление в пространстве имён миграций
// находится раньше, чем импортированное через using, поэтому исторические файлы
// компилируются без правок. В коде приложения эти типы не использовать.

internal enum City
{
    Tiraspol,
    Bendery,
    Rybnitsa,
    Dubossary,
    Slobodzea,
    Grigoriopol,
    Dnestrovsk,
    Kamenka
}

internal enum Category
{
    RealEstate,
    Transport,
    Electronics,
    Home,
    Fashion,
    Kids,
    Work,
    Services,
    Animals,
    Other
}
