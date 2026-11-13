using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using NewStore.ServiceDefaults.Data;

namespace NewStore.Inventory.Tests;

public class SeedTests(ConditionalUpdateFixture fixture) : IClassFixture<ConditionalUpdateFixture>
{
    [Fact]
    public async Task Seed_creates_the_sample_editions_once()
    {
        var ct = TestContext.Current.CancellationToken;

        // Twice, each in its own scope as the "seed" command does: the second run must find the data and stop.
        for (var run = 0; run < 2; run++)
        {
            await using var scope = fixture.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            await scope.ServiceProvider.GetRequiredService<IDatabaseSeeder<InventoryDbContext>>().SeedAsync(db, ct);
        }

        await using var check = fixture.Services.CreateAsyncScope();
        var editions = await check.ServiceProvider.GetRequiredService<InventoryDbContext>().Editions.AsNoTracking().ToListAsync(ct);
        Assert.Equal(60, editions.Count);
        Assert.Equal(12, editions.Count(e => e.Format == EditionFormat.Dvd));

        // "Sourdough in Zero Gravity", the first video of the first sample creator.
        var sourdough = editions.Single(e =>
            e.VideoId == Guid.Parse("00000000-0000-7000-8001-000000000011") && e.Format == EditionFormat.Digital);
        Assert.Equal((Guid.Parse("00000000-0000-7000-8000-000000000001"), 4.99m, 1000, 1000),
            (sourdough.CreatorId, sourdough.Price, sourdough.CopiesTotal, sourdough.CopiesLeft));
    }
}
