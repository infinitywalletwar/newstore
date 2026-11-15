using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using NewStore.ServiceDefaults.Data;

using static NewStore.Media.Tests.Api;

namespace NewStore.Media.Tests;

/// <summary>The seed's HLS sample (made with FFmpeg) and the Published uploads of the sample videos, on real S3 and PostgreSQL.</summary>
public sealed class SeedTests(SeedFixture media) : IClassFixture<SeedFixture>
{
    private const string SampleMaster = "hls/sample/master.m3u8";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Seed_makes_the_sample_once()
    {
        // A bucket no seed has touched yet.
        var bucket = $"seed-{Guid.NewGuid():N}";
        await using var app = media.WithWebHostBuilder(b => b.UseSetting("Media:Storage:Bucket", bucket));
        var before = media.Store.Uploads;

        await SeedAsync(app);
        var afterFirst = media.Store.Uploads;
        await SeedAsync(app);

        // The original; per variant two segments (6 s and 4 s) and a playlist; the thumbnail; the master.
        Assert.Equal(1 + (2 * 3) + 1 + 1, afterFirst - before);
        Assert.Equal(afterFirst, media.Store.Uploads);   // the second run found the master and uploaded nothing

        using var master = await ProcessingFixture.GetAnonymousAsync(new Uri(media.S3.PublicUrl, $"{bucket}/{SampleMaster}"));
        Assert.Equal(HttpStatusCode.OK, master.StatusCode);
        var playlist = await master.Content.ReadAsStringAsync(Ct);
        Assert.Contains("RESOLUTION=640x360", playlist);
        Assert.Contains("RESOLUTION=1280x720", playlist);
        using var thumbnail = await ProcessingFixture.GetAnonymousAsync(new Uri(media.S3.PublicUrl, $"{bucket}/hls/sample/thumbnail.jpg"));
        Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);

        // The original is there, and private like every original.
        using var original = await ProcessingFixture.GetAnonymousAsync(new Uri(media.S3.PublicUrl, $"{bucket}/{ObjectKeys.SampleOriginal}"));
        Assert.Equal(HttpStatusCode.Forbidden, original.StatusCode);
        using var s3 = media.S3.CreateClient(media.S3.InternalUrl);
        var head = await s3.GetObjectMetadataAsync(bucket, ObjectKeys.SampleOriginal, Ct);
        Assert.Equal("video/mp4", head.Headers.ContentType);
    }

    [Fact]
    public async Task Seed_gives_every_sample_video_a_published_upload()
    {
        await SeedAsync(media);
        await SeedAsync(media);

        var uploads = await media.WithDbAsync(db => db.Uploads.AsNoTracking().Where(u => u.ObjectKey == ObjectKeys.SampleOriginal).ToListAsync(Ct));

        Assert.Equal(48, uploads.Count);   // every entry of sample-videos.json, once, however often the seed runs
        Assert.Equal(48, uploads.Select(u => u.VideoId).Distinct().Count());
        Assert.All(uploads, u =>
        {
            Assert.Equal(UploadStatus.Published, u.Status);
            Assert.Equal((SampleMaster, "hls/sample/thumbnail.jpg", 10), (u.PlaylistPath, u.ThumbnailPath, u.DurationSeconds));
            Assert.Equal($"{u.VideoId}.mp4", u.FileName);
            Assert.True(u.SizeBytes > 0);
            Assert.NotNull(u.PublishedAt);
        });

        // The first sample video of the catalog's seed, with its channel's creator.
        var first = Assert.Single(uploads, u => u.VideoId == Guid.Parse("00000000-0000-7000-8001-000000000011"));
        Assert.Equal(Guid.Parse("00000000-0000-7000-8000-000000000001"), first.CreatorId);
    }

    [Fact]
    public async Task Sample_video_owner_downloads_the_sample_original()
    {
        await SeedAsync(media);
        var sampleVideo = Guid.Parse("00000000-0000-7000-8001-000000000011");
        var owner = Guid.NewGuid();
        await media.WithDbAsync(async db =>
        {
            db.DigitalCopies.Add(new DigitalCopy { OwnerId = owner, VideoId = sampleVideo, EditionId = Guid.NewGuid(), CopyNumber = 1, GrantedAt = media.Clock.GetUtcNow() });
            await db.SaveChangesAsync(Ct);
            return 0;
        });

        var response = await media.CreateClient().SendAsUserAsync(owner, HttpMethod.Post, "/downloads", new { videoId = sampleVideo });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var link = (await response.Content.ReadFromJsonAsync<DownloadLink>(Ct))!;
        using var file = await ProcessingFixture.GetAnonymousAsync(new Uri(link.Url));
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.True((await file.Content.ReadAsByteArrayAsync(Ct)).Length > 0);
    }

    private static async Task SeedAsync(WebApplicationFactory<Program> app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var seeder = scope.ServiceProvider.GetRequiredService<IDatabaseSeeder<MediaDbContext>>();
        await seeder.SeedAsync(scope.ServiceProvider.GetRequiredService<MediaDbContext>(), Ct);
    }
}

/// <summary>Media without a worker, with the processing settings, its own work directory and the spied store.</summary>
public sealed class SeedFixture(PostgresServer postgres, KafkaServer kafka, S3Server s3) : ProcessingFixture(postgres, kafka, s3)
{
    protected override MediaRole Role => MediaRole.Api;
}
