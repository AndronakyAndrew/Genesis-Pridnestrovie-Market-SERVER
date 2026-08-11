using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace GenesisMarket.Api.Listings;

/// <summary>
/// Подсветка совпадений в заголовках через <c>ts_headline</c>. XSS-безопасно:
/// исходный <c>Title</c> экранируется (&amp; &lt; &gt;) ПРЯМО В SQL до подсветки,
/// поэтому единственный HTML в результате — это вставленные СУБД теги &lt;mark&gt;.
/// Запрос параметризован (@q, @ids) — никакой интерполяции пользовательского ввода.
/// </summary>
public static class SearchHighlighter
{
    // Экранируем '&' первым, иначе повторно экранируем уже вставленные сущности.
    private const string EscapedTitle =
        "replace(replace(replace(coalesce(\"Title\", ''), '&', '&amp;'), '<', '&lt;'), '>', '&gt;')";

    private const string Sql = $"""
        SELECT "Id",
               ts_headline('russian', {EscapedTitle},
                   websearch_to_tsquery('russian', @q),
                   'StartSel=<mark>, StopSel=</mark>, HighlightAll=TRUE') AS headline
        FROM listings
        WHERE "Id" = ANY(@ids)
        """;

    /// <summary>id → подсвеченный (и экранированный) заголовок для указанных объявлений.</summary>
    public static async Task<Dictionary<Guid, string>> HighlightTitlesAsync(
        AppDbContext db, IReadOnlyList<Guid> ids, string query, CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>(ids.Count);
        if (ids.Count == 0)
            return result;

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("q", query);
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            Value = ids.ToArray()
        });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetGuid(0)] = reader.GetString(1);

        return result;
    }
}
