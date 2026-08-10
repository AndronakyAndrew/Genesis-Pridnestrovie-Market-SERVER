using Npgsql;

namespace GenesisMarket.Infrastructure.Persistence;

/// <summary>
/// Транслятор имён для native enum-ов PostgreSQL: метка = имя члена в нижнем
/// регистре без разделителей (RealEstate → "realestate", NotApplicable →
/// "notapplicable"). Это совпадает со справочниками категорий и городов из
/// CLAUDE.md. Используется и при генерации миграции (метки CREATE TYPE),
/// и при runtime-маппинге значений Npgsql.
/// </summary>
public sealed class LowerCaseNameTranslator : INpgsqlNameTranslator
{
    public static readonly LowerCaseNameTranslator Instance = new();

    public string TranslateTypeName(string clrName) => clrName.ToLowerInvariant();

    public string TranslateMemberName(string clrName) => clrName.ToLowerInvariant();
}
