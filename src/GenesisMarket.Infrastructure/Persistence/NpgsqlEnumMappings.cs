using GenesisMarket.Domain.Enums;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace GenesisMarket.Infrastructure.Persistence;

/// <summary>
/// Регистрация native enum-типов на уровне подключения Npgsql.
/// Обязательна и в рантайме, и в design-time фабрике: без неё свойства-enum
/// маппятся в integer, и CHECK-констрейнты по меткам ('fixed' и т.п.) ломаются.
/// Имена типов и транслятор меток должны совпадать с HasPostgresEnum в AppDbContext.
/// </summary>
public static class NpgsqlEnumMappings
{
    public static NpgsqlDbContextOptionsBuilder MapGenesisEnums(this NpgsqlDbContextOptionsBuilder npgsql)
    {
        var tr = LowerCaseNameTranslator.Instance;
        npgsql.MapEnum<Category>("category", nameTranslator: tr);
        npgsql.MapEnum<City>("city", nameTranslator: tr);
        npgsql.MapEnum<Condition>("condition", nameTranslator: tr);
        npgsql.MapEnum<ListingStatus>("listing_status", nameTranslator: tr);
        npgsql.MapEnum<PriceType>("price_type", nameTranslator: tr);
        return npgsql;
    }
}
