using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Domain.Enums;
using Xunit;

namespace GenesisMarket.Tests;

public class SubcategoriesTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // API отдаёт enum'ы строками (JsonStringEnumConverter в Program.cs), поэтому
    // читать ответ дефолтными настройками нельзя — они ждут число и падают.
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Guest_can_get_seeded_subcategories()
    {
        var response = await factory.CreateClient().GetAsync("/api/subcategories");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var subcategories = await response.Content.ReadFromJsonAsync<List<SubcategoryResponse>>(Json);
        Assert.NotNull(subcategories);
        Assert.Contains(subcategories, s => s.Id == 18
            && s.Category == Category.Home
            && s.Slug == "mebel"
            && s.Name == "Мебель");
    }
}
