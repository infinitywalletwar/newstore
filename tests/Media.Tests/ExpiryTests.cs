using Amazon.S3;
using Amazon.S3.Model;

using Microsoft.EntityFrameworkCore;

using static NewStore.Media.Tests.Api;

namespace NewStore.Media.Tests;

/// <summary>The expiry service on a fake clock: it ticks every hour of that clock, and an upload is abandoned after 24 hours.</summary>
public sealed class ExpiryTests(ExpiryFixture media) : IClassFixture<ExpiryFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<Upload> RowAsync(Guid id) => media.WithDbAsync(db => db.Uploads.AsNoTracking().SingleAsync(u => u.Id == id, Ct));

    /// <summary>Polls, in real time, for something the background service does after the fake clock moved.</summary>
    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        }

        Assert.Fail($"Not within 15 s: {what}.");
    }

    private async Task<(Guid Creator, UploadResponse Upload)> CreateWithOnePartAsync()
    {
        var creator = Guid.NewGuid();
        var upload = await media.CreateClient().CreateAsync(creator);
        await PutPartOkAsync(upload.PartUrls[0]);
        return (creator, upload);
    }

    [Fact]
    public async Task Abandoned_upload_expires_after_24_hours()
    {
        var (_, upload) = await CreateWithOnePartAsync();
        var s3UploadId = (await RowAsync(upload.Id)).S3UploadId;

        media.Clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));

        await EventuallyAsync(async () => (await RowAsync(upload.Id)).S3AbortedAt is not null, "the upload is expired and aborted");
        var row = await RowAsync(upload.Id);
        Assert.Equal(UploadStatus.Expired, row.Status);
        Assert.Equal("abandoned", row.FailureReason);
        using var s3 = media.S3.CreateClient(media.S3.InternalUrl);
        var gone = await Assert.ThrowsAsync<AmazonS3Exception>(() => s3.ListPartsAsync(
            new ListPartsRequest { BucketName = media.Bucket, Key = row.ObjectKey, UploadId = s3UploadId }, Ct));
        Assert.Equal("NoSuchUpload", gone.ErrorCode);
    }

    [Fact]
    public async Task Upload_younger_than_24_hours_stays()
    {
        var (_, young) = await CreateWithOnePartAsync();

        // A sentinel that the sweep must expire: an Uploading row 25 hours old (its S3 upload id is made up, so its abort answers
        // NoSuchUpload, which counts as done). When the sentinel is Expired, a sweep has run since the clock moved, and only then is
        // "the young upload is still Uploading" a statement about the sweep and not about a sweep that never ran.
        var sentinel = await InsertUploadingRowAsync(createdAt: Micro(media.Clock.GetUtcNow()) - TimeSpan.FromHours(25));

        media.Clock.Advance(TimeSpan.FromHours(23));

        await EventuallyAsync(async () => (await RowAsync(sentinel)).Status == UploadStatus.Expired, "the sentinel expires");
        Assert.Equal(UploadStatus.Uploading, (await RowAsync(young.Id)).Status);

        // Control: the young upload is 24 hours old after one more hour (and a minute), and then it does expire.
        media.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        await EventuallyAsync(async () => (await RowAsync(young.Id)).Status == UploadStatus.Expired, "the first upload expires");
    }

    private async Task<Guid> InsertUploadingRowAsync(DateTimeOffset createdAt)
    {
        var id = Guid.CreateVersion7();
        await media.WithDbAsync(async db =>
        {
            db.Uploads.Add(new Upload
            {
                Id = id,
                VideoId = Guid.NewGuid(),
                CreatorId = Guid.NewGuid(),
                FileName = "s.mp4",
                ContentType = "video/mp4",
                SizeBytes = 1,
                ObjectKey = $"originals/sentinel/{id}",
                S3UploadId = "no-such-upload",
                PartSize = MediaFixture.PartSize,
                PartCount = 1,
                Status = UploadStatus.Uploading,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            });
            return await db.SaveChangesAsync(Ct);
        });
        return id;
    }

    [Fact]
    public async Task Failed_abort_is_retried_on_the_next_tick()
    {
        var (_, upload) = await CreateWithOnePartAsync();
        var refuser = media.Refuser;
        refuser.Armed = true;

        media.Clock.Advance(TimeSpan.FromHours(25));

        await EventuallyAsync(() => Task.FromResult(refuser.Attempts >= 1), "S3 refuses the first abort");
        var refused = await RowAsync(upload.Id);
        Assert.Equal(UploadStatus.Expired, refused.Status);
        Assert.Null(refused.S3AbortedAt);

        refuser.Armed = false;
        media.Clock.Advance(TimeSpan.FromHours(1));

        await EventuallyAsync(async () => (await RowAsync(upload.Id)).S3AbortedAt is not null, "the next tick aborts it");

        // MarkAborted stamps the pass's "now": this is the second tick's time, so the first pass (the clock 25 hours on) did not do it.
        Assert.Equal(Micro(media.Clock.GetUtcNow()), (await RowAsync(upload.Id)).S3AbortedAt);
    }

    [Fact]
    public async Task Refused_cancel_abort_is_retried_by_the_sweep()
    {
        // DELETE answers 204 and the row is Expired/cancelled, but S3 refused the abort: S3AbortedAt stays empty, and the sweep aborts it.
        var (creator, upload) = await CreateWithOnePartAsync();
        var s3UploadId = (await RowAsync(upload.Id)).S3UploadId;
        var refuser = media.Refuser;
        refuser.Armed = true;

        var response = await media.CreateClient().SendAsUserAsync(creator, HttpMethod.Delete, $"/uploads/{upload.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var refused = await RowAsync(upload.Id);
        Assert.Equal(UploadStatus.Expired, refused.Status);
        Assert.Equal("cancelled", refused.FailureReason);
        Assert.Null(refused.S3AbortedAt);

        refuser.Armed = false;
        media.Clock.Advance(TimeSpan.FromHours(1));

        await EventuallyAsync(async () => (await RowAsync(upload.Id)).S3AbortedAt is not null, "the sweep aborts the cancelled upload");
        using var s3 = media.S3.CreateClient(media.S3.InternalUrl);
        var gone = await Assert.ThrowsAsync<AmazonS3Exception>(() => s3.ListPartsAsync(
            new ListPartsRequest { BucketName = media.Bucket, Key = refused.ObjectKey, UploadId = s3UploadId }, Ct));
        Assert.Equal("NoSuchUpload", gone.ErrorCode);
    }
}
