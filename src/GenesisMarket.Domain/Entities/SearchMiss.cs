using GenesisMarket.Domain.Common;

namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Поисковый запрос, вернувший нулевую выдачу (ни FTS, ни fuzzy-fallback ничего не нашли).
/// Это данные для наполнения каталога: чего пользователи ищут, но у нас нет.
/// </summary>
public class SearchMiss : BaseEntity
{
    /// <summary>Нормализованный (trim + lower) поисковый запрос, не длиннее 100 символов.</summary>
    public required string Query { get; set; }
}
