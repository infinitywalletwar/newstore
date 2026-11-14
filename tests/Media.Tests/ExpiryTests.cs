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
        media.Clock.Advance(TimeSpan.FromHours(23));
        var (_, old) = await CreateWithOnePartAsync();

        // The service ticked 23 times. Give those passes real time to run, then look.
        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);
        Assert.Equal(UploadStatus.Uploading, (await RowAsync(young.Id)).Status);

        // Control: the same service, once the young upload is 24 hours old, does expire it. So the check above was not a dead timer.
        // (The other upload is 1 hour younger than that and stays.)
        media.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        await EventuallyAsync(async () => (await RowAsync(young.Id)).Status == UploadStatus.Expired, "the first upload expires");
        Assert.Equal(UploadStatus.Uploading, (await RowAsync(old.Id)).Status);
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
    }
}
