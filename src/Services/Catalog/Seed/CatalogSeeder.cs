using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using NewStore.Catalog.Channels;
using NewStore.Catalog.Videos;
using NewStore.ServiceDefaults.Data;

namespace NewStore.Catalog.Seed;

/// <summary>
/// Inserts the sample catalog from <c>sample-catalog.json</c> (published videos that play the sample files) when the database has no channels. Running it
/// again does nothing, so the Compose job can run "seed" on every start.
/// </summary>
public sealed class CatalogSeeder(TimeProvider time) : IDatabaseSeeder<CatalogDbContext>
{
    private const string FileName = "sample-catalog.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task SeedAsync(CatalogDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Channels.AnyAsync(cancellationToken))
        {
            return;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Seed", FileName);
        await using var file = File.OpenRead(path);
        var sample = await JsonSerializer.DeserializeAsync<SampleCatalog>(file, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"{FileName} is empty.");

        var now = time.GetUtcNow();
        foreach (var sampleChannel in sample.Channels)
        {
            var channel = new Channel
            {
                Id = Guid.CreateVersion7(),
                CreatorId = sampleChannel.CreatorId,
                Name = sampleChannel.Name,
                Slug = sampleChannel.Slug,
                Description = sampleChannel.Description,
                CreatedAt = now,
            };
            db.Channels.Add(channel);
            db.Videos.AddRange(sampleChannel.Videos.Select(v => new Video
            {
                Id = v.Id,
                ChannelId = channel.Id,
                Title = v.Title,
                Description = v.Description,
                DurationSeconds = v.DurationSeconds,
                Status = VideoStatus.Published,
                CreatedAt = v.PublishedAt,
                PublishedAt = v.PublishedAt,
                PlaylistPath = Video.SamplePlaylistPath,
                ThumbnailPath = Video.SampleThumbnailPath,
            }));
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private sealed record SampleCatalog(List<SampleChannel> Channels);

    private sealed record SampleChannel(
        Guid CreatorId, string Name, string Slug, string Description, List<SampleVideo> Videos);

    private sealed record SampleVideo(Guid Id, string Title, string Description, int DurationSeconds, DateTimeOffset PublishedAt);
}
