using System.Text.Json;
using System.Text.Json.Serialization;
using GenesisMarket.Api.Contracts;

namespace GenesisMarket.Api.SavedSearches;

/// <summary>
/// Сериализация критериев сохранённого поиска в столбец <c>QueryJson</c> и обратно.
/// Enum-ы пишутся строками (стабильно и читаемо в jsonb). Единые опции у продюсера
/// (контроллер) и консьюмера (джоб), чтобы формат не разъехался.
/// </summary>
public static class SavedSearchJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(SavedSearchQuery query) =>
        JsonSerializer.Serialize(query, Options);

    /// <summary>Разбор из jsonb. false при любой некорректности — джоб такому поиску не доверяет.</summary>
    public static bool TryDeserialize(string json, out SavedSearchQuery query)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<SavedSearchQuery>(json, Options);
            if (parsed is null)
            {
                query = new SavedSearchQuery();
                return false;
            }
            query = parsed;
            return true;
        }
        catch (JsonException)
        {
            query = new SavedSearchQuery();
            return false;
        }
    }
}
