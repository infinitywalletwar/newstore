using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

using NewStore.Catalog.Videos;

namespace NewStore.Catalog.Tests;

public class VideoMigrationTests(PostgresCatalogAppFactory factory) : IClassFixture<PostgresCatalogAppFactory>
{
    [Fact]
    public async Task Migration_fills_existing_videos()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var migrator = db.GetService<IMigrator>();
        var channelId = Guid.NewGuid();
        var videoId = Guid.NewGuid();
        var publishedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        // The database as Phase 1 left it: the migration before VideoDrafts, with a video in it.
        await migrator.MigrateAsync("20261113030248_UniqueChannelCreator", ct);
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO channels (id, creator_id, name, slug, description, created_at)
            VALUES ({channelId}, {Guid.NewGuid()}, 'Old', 'old-channel', '', {publishedAt});
            INSERT INTO videos (id, channel_id, title, description, duration_seconds, published_at)
            VALUES ({videoId}, {channelId}, 'Old video', '', 60, {publishedAt});
            """, ct);

        await migrator.MigrateAsync(null, ct);

        var video = await db.Videos.AsNoTracking().SingleAsync(v => v.Id == videoId, ct);
        Assert.Equal(VideoStatus.Published, video.Status);
        Assert.Equal(publishedAt, video.CreatedAt);
        Assert.Equal(publishedAt, video.PublishedAt);
        Assert.Equal("hls/sample/master.m3u8", video.PlaylistPath);
        Assert.Equal("hls/sample/thumbnail.jpg", video.ThumbnailPath);
    }
}
