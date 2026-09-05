using System.Net;
using System.Net.Http.Json;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GenesisMarket.Tests;

public class StatsTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    /// <summary>
    /// Единственный тест класса: ответ кэшируется на 60 секунд, поэтому второй
    /// запрос увидел бы уже посчитанные значения, а не свежий сид.
    /// </summary>
    [Fact]
    public async Task Guest_gets_counters_without_staff_and_unpublished()
    {
        var seller = await factory.SeedUserAsync("stats-seller@genesis.test", "Passw0rd!x");
        await factory.SeedUserAsync("stats-buyer@genesis.test", "Passw0rd!x");
        await factory.SeedUserAsync("stats-mod@genesis.test", "Passw0rd!x", role: UserRole.Moderator);
        await factory.SeedUserAsync("stats-admin@genesis.test", "Passw0rd!x", role: UserRole.Admin);

        var deleted = await factory.SeedUserAsync("stats-deleted@genesis.test", "Passw0rd!x");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Users.Where(u => u.Id == deleted)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDeleted, true));
        }

        await factory.SeedListingAsync(seller);
        await factory.SeedListingAsync(seller);
        await factory.SeedListingAsync(seller, ListingStatus.Draft);
        await factory.SeedListingAsync(seller, ListingStatus.PendingReview);

        var response = await factory.CreateClient().GetAsync("/api/stats");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stats = await response.Content.ReadFromJsonAsync<PublicStatsResponse>();
        Assert.NotNull(stats);
        Assert.Equal(2, stats.UsersCount);
        Assert.Equal(2, stats.ListingsCount);
    }
}
