using NewStore.ServiceDefaults.Data;

namespace NewStore.Media;

/// <summary>Seeds Media: the bucket's settings. Task 5 adds the sample video.</summary>
public sealed class MediaSeeder(StorageSetup storage) : IDatabaseSeeder<MediaDbContext>
{
    public Task SeedAsync(MediaDbContext db, CancellationToken cancellationToken) => storage.EnsureAsync(cancellationToken);
}
