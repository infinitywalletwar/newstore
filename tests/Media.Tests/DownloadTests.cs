using System.Net.Http.Json;
using System.Web;

using Amazon.S3.Model;

using Microsoft.EntityFrameworkCore;

using NewStore.Contracts.Inventory;

using static NewStore.Media.Tests.Api;

namespace NewStore.Media.Tests;

/// <summary>
/// An owner's download, from the <c>CopyAssigned</c> event on Kafka to the bytes from S3. The test publishes the event the way
/// Inventory's relay does and plays the browser when it follows the link.
/// </summary>
public sealed class DownloadTests(MediaFixture media, KafkaServer kafka) : IClassFixture<MediaFixture>
{
    private sealed record Link(string Url, DateTimeOffset ExpiresAt, string FileName);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly HttpClient Browser = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });

    private HttpClient Client => media.CreateClient();

    private static CopyAssigned Copy(Guid owner, Guid videoId, string format = "Digital", Guid? editionId = null) => new()
    {
        EventId = Guid.NewGuid(),
        OccurredAt = DateTimeOffset.UtcNow,
        ReservationId = Guid.NewGuid(),
        EditionId = editionId ?? Guid.NewGuid(),
        VideoId = videoId,
        Format = format,
        OwnerId = owner,
        CopyNumber = 1,
    };

    private Task PublishAsync(CopyAssigned e) => kafka.PublishAsync(CopyAssigned.Topic, e.EditionId.ToString(), e, Ct);

    /// <summary>Waits until the consumer has handled the message: its inbox row is the proof, whatever the handler did.</summary>
    private async Task WaitForHandledAsync(CopyAssigned e)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await InboxCountAsync(e.EventId) == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "CopyAssigned was not handled within 30 seconds.");
            await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        }
    }

    private Task<long> InboxCountAsync(Guid messageId) => media.WithDbAsync(db => db.Database
        .SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM inbox_messages WHERE message_id = {messageId}").SingleAsync(Ct));

    private Task<int> CopiesAsync(Guid owner, Guid videoId) =>
        media.WithDbAsync(db => db.DigitalCopies.CountAsync(c => c.OwnerId == owner && c.VideoId == videoId, Ct));

    /// <summary>A video with a Published upload whose original is in the bucket. Returns the video's id.</summary>
    private async Task<Guid> PublishedVideoAsync(string fileName = "trip.mp4", string content = "original bytes", UploadStatus status = UploadStatus.Published)
    {
        var videoId = Guid.NewGuid();
        var uploadId = Guid.NewGuid();
        var key = ObjectKeys.Original(videoId, uploadId);
        using var s3 = media.S3.CreateClient(media.S3.InternalUrl);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = media.Bucket, Key = key, ContentBody = content }, Ct);
        await media.WithDbAsync(async db =>
        {
            var now = Micro(media.Clock.GetUtcNow());
            db.Uploads.Add(new Upload
            {
                Id = uploadId,
                VideoId = videoId,
                CreatorId = Guid.NewGuid(),
                FileName = fileName,
                ContentType = "video/mp4",
                SizeBytes = content.Length,
                ObjectKey = key,
                S3UploadId = "done",
                PartSize = MediaFixture.PartSize,
                PartCount = 1,
                Status = status,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync(Ct);
            return 0;
        });
        return videoId;
    }

    private Task<HttpResponseMessage> RequestAsync(Guid user, Guid videoId) =>
        Client.SendAsUserAsync(user, HttpMethod.Post, "/downloads", new { videoId });

    /// <summary>The owner has a Digital copy that Media has recorded.</summary>
    private async Task<Guid> OwnerAsync(Guid videoId)
    {
        var owner = Guid.NewGuid();
        var e = Copy(owner, videoId);
        await PublishAsync(e);
        await WaitForHandledAsync(e);
        return owner;
    }

    [Fact]
    public async Task Digital_copy_lets_the_owner_download_the_original()
    {
        var videoId = await PublishedVideoAsync(content: "the original video");
        var owner = await OwnerAsync(videoId);

        var response = await RequestAsync(owner, videoId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var link = (await response.Content.ReadFromJsonAsync<Link>(Ct))!;
        Assert.Equal("trip.mp4", link.FileName);
        using var file = await Browser.GetAsync(link.Url, Ct);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("the original video", await file.Content.ReadAsStringAsync(Ct));
        Assert.Equal("attachment; filename=\"trip.mp4\"", file.Content.Headers.GetValues("Content-Disposition").Single());
    }

    [Fact]
    public async Task Link_lasts_15_minutes()
    {
        var videoId = await PublishedVideoAsync();
        var owner = await OwnerAsync(videoId);
        var expected = Micro(media.Clock.GetUtcNow()).AddMinutes(15);

        var link = (await (await RequestAsync(owner, videoId)).Content.ReadFromJsonAsync<Link>(Ct))!;

        // The SDK counts X-Amz-Expires from the real clock; expiresAt comes from the fake one. Two separate checks.
        var expires = int.Parse(HttpUtility.ParseQueryString(new Uri(link.Url).Query)["X-Amz-Expires"]!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(expires, 890, 900);
        Assert.Equal(expected, link.ExpiresAt);
    }

    [Fact]
    public async Task Dvd_copy_gives_no_download()
    {
        var videoId = await PublishedVideoAsync();
        var owner = Guid.NewGuid();
        var e = Copy(owner, videoId, "Dvd");
        await PublishAsync(e);
        await WaitForHandledAsync(e);

        var response = await RequestAsync(owner, videoId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("urn:newstore:problem:no-digital-copy", (await response.ProblemAsync()).Type);
        Assert.Equal(0, await CopiesAsync(owner, videoId));
    }

    [Fact]
    public async Task Someone_else_cannot_get_a_link()
    {
        var videoId = await PublishedVideoAsync();
        await OwnerAsync(videoId);

        var response = await RequestAsync(Guid.NewGuid(), videoId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("urn:newstore:problem:no-digital-copy", (await response.ProblemAsync()).Type);
    }

    [Fact]
    public async Task Duplicate_CopyAssigned_is_handled_once()
    {
        var videoId = await PublishedVideoAsync();
        var owner = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var first = Copy(owner, videoId, editionId: edition);
        var marker = Copy(Guid.NewGuid(), videoId, editionId: edition);

        // The same message id twice, then a marker with the same key: one partition keeps the order, so the marker's inbox row
        // means the duplicate has been consumed too.
        await PublishAsync(first);
        await PublishAsync(first);
        await PublishAsync(marker);
        await WaitForHandledAsync(marker);

        Assert.Equal(1, await InboxCountAsync(first.EventId));
        Assert.Equal(1, await CopiesAsync(owner, videoId));
    }

    [Fact]
    public async Task Video_without_an_original_is_404()
    {
        var noUpload = Guid.NewGuid();
        var ownerOfNone = await OwnerAsync(noUpload);
        var processing = await PublishedVideoAsync(status: UploadStatus.Processing);
        var ownerOfProcessing = await OwnerAsync(processing);

        foreach (var (owner, video) in new[] { (ownerOfNone, noUpload), (ownerOfProcessing, processing) })
        {
            var response = await RequestAsync(owner, video);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("urn:newstore:problem:no-original", (await response.ProblemAsync()).Type);
        }
    }

    [Fact]
    public async Task File_name_is_made_safe()
    {
        var videoId = await PublishedVideoAsync("my \"trip\".mp4");
        var owner = await OwnerAsync(videoId);

        var link = (await (await RequestAsync(owner, videoId)).Content.ReadFromJsonAsync<Link>(Ct))!;

        Assert.Equal("my__trip_.mp4", link.FileName);
        using var file = await Browser.GetAsync(link.Url, Ct);
        Assert.Equal("attachment; filename=\"my__trip_.mp4\"", file.Content.Headers.GetValues("Content-Disposition").Single());
    }

    [Fact]
    public async Task Downloads_need_a_signed_in_user()
    {
        var response = await Client.PostAsJsonAsync("/downloads", new { videoId = Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
