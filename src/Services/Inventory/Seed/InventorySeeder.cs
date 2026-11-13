using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using NewStore.ServiceDefaults.Data;

namespace NewStore.Inventory;

/// <summary>
/// Inserts the sample editions from <c>sample-editions.json</c> when the database has no editions: one Digital edition per
/// sample video and a Dvd edition for every fourth one. Running it again does nothing, so the Compose job can run "seed" on every start.
/// </summary>
public sealed class InventorySeeder(TimeProvider time) : IDatabaseSeeder<InventoryDbContext>
{
    private const string FileName = "sample-editions.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task SeedAsync(InventoryDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Editions.AnyAsync(cancellationToken))
        {
            return;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Seed", FileName);
        await using var file = File.OpenRead(path);
        var sample = await JsonSerializer.DeserializeAsync<List<SampleEdition>>(file, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"{FileName} is empty.");

        var now = time.GetUtcNowToMicrosecond();
        db.Editions.AddRange(sample.Select(e => new Edition
        {
            Id = Guid.CreateVersion7(),
            VideoId = e.VideoId,
            CreatorId = e.CreatorId,
            Format = Enum.Parse<EditionFormat>(e.Format),
            Price = e.Price,
            CopiesTotal = e.Copies,
            CopiesLeft = e.Copies,
            CreatedAt = now,
        }));
        await db.SaveChangesAsync(cancellationToken);
    }

    private sealed record SampleEdition(Guid VideoId, Guid CreatorId, string Format, decimal Price, int Copies);
}
