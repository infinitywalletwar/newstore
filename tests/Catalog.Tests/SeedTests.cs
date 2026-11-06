using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using NewStore.ServiceDefaults.Data;

namespace NewStore.Catalog.Tests;

public class SeedTests(PostgresCatalogAppFactory factory) : IClassFixture<PostgresCatalogAppFactory>
{
    [Fact]
    public async Task Seed_inserts_the_sample_catalog_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await factory.MigrateAsync<CatalogDbContext>();

        // Twice, each in its own scope as the "seed" command does: the second run must find the data and stop.
        for (var run = 0; run < 2; run++)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await scope.ServiceProvider.GetRequiredService<IDatabaseSeeder<CatalogDbContext>>().SeedAsync(db, ct);
        }

        await using var check = factory.Services.CreateAsyncScope();
        var context = check.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Equal(6, await context.Channels.CountAsync(ct));
        Assert.Equal(48, await context.Videos.CountAsync(ct));
    }
}
