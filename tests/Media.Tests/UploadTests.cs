using System.Net.Http.Json;
using System.Web;

using Amazon.S3;
using Amazon.S3.Model;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using static NewStore.Media.Tests.Api;

namespace NewStore.Media.Tests;

/// <summary>The whole upload, with the test playing the browser: it sends the parts to the presigned URLs itself.</summary>
public sealed class UploadTests(MediaFixture media, ShortLifetimeFixture shortLived) : IClassFixture<MediaFixture>, IClassFixture<ShortLifetimeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Client => media.CreateClient();

    [Fact]
    public async Task Create_returns_a_url_for_every_part()
    {
        var creator = Guid.NewGuid();
        var expectedExpiry = Micro(media.Clock.GetUtcNow());

        var upload = await Client.CreateAsync(creator);

        Assert.Equal("Uploading", upload.Status);
        Assert.Equal(MediaFixture.PartSize, upload.PartSize);
        Assert.Equal(3, upload.PartCount);
        Assert.Equal([5 * MiB, 5 * MiB, 2 * MiB], upload.PartUrls.Select(p => p.Size));
        Assert.Equal([1, 2, 3], upload.PartUrls.Select(p => p.PartNumber));
        Assert.Empty(upload.UploadedParts);
        foreach (var part in upload.PartUrls)
        {
            // The browser's host, not the one Media talks to: SigV4 signs the host, so the other would fail.
            var url = new Uri(part.Url);
            Assert.Equal(media.S3.PublicUrl.Host, url.Host);
            Assert.Equal(media.S3.PublicUrl.Port, url.Port);

            // The SDK counts X-Amz-Expires from its own (real) clock.
            var expires = int.Parse(HttpUtility.ParseQueryString(url.Query)["X-Amz-Expires"]!, System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(expires, 3590, 3600);
        }

        // The API's expiresAt comes from TimeProvider, which the test's fake clock replaces: the two are asserted independently.
        Assert.Equal(expectedExpiry.AddHours(1), upload.PartUrlsExpireAt);
    }

    [Fact]
    public async Task Parts_put_and_complete_move_the_upload_to_processing_with_a_job()
    {
        var creator = Guid.NewGuid();
        var client = Client;
        var upload = await client.CreateAsync(creator);
        var etags = new List<(int, string)>();
        foreach (var part in upload.PartUrls)
        {
            etags.Add(await PutPartOkAsync(part));
        }

        var response = await client.PostCompleteAsync(creator, upload.Id, etags);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<UploadResponse>(Ct))!;
        Assert.Equal("Processing", body.Status);
        Assert.Empty(body.PartUrls);
        var (status, jobs, completedAt) = await media.WithDbAsync(async db => (
            (await db.Uploads.SingleAsync(u => u.Id == upload.Id, Ct)).Status,
            await db.ProcessingJobs.Where(j => j.UploadId == upload.Id).ToListAsync(Ct),
            (await db.Uploads.SingleAsync(u => u.Id == upload.Id, Ct)).CompletedAt));
        Assert.Equal(UploadStatus.Processing, status);
        var job = Assert.Single(jobs);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(Micro(media.Clock.GetUtcNow()), job.NextRunAt);
        Assert.NotNull(completedAt);
        using var s3 = media.S3.CreateClient(media.S3.InternalUrl);
        var head = await s3.GetObjectMetadataAsync(media.Bucket, ObjectKeys.Original(upload.VideoId, upload.Id), Ct);
        Assert.Equal(12 * MiB, head.ContentLength);
    }

    [Fact]
    public async Task State_lists_uploaded_parts_and_urls_for_the_rest()
    {
        // Review Focus 1: the reload path. Parts 1 and 3 are on S3; GET says so and offers a URL for part 2 only.
        var creator = Guid.NewGuid();
        var client = Client;
        var upload = await client.CreateAsync(creator);
        var one = await PutPartOkAsync(upload.PartUrls[0]);
        var three = await PutPartOkAsync(upload.PartUrls[2]);

        var state = await client.GetAsync(creator, upload.Id);

        Assert.Equal([1, 3], state.UploadedParts.Select(p => p.PartNumber));
        Assert.Equal([one.ETag, three.ETag], state.UploadedParts.Select(p => p.ETag));
        Assert.Equal([5 * MiB, 2 * MiB], state.UploadedParts.Select(p => p.Size));
        var missing = Assert.Single(state.PartUrls);
        Assert.Equal(2, missing.PartNumber);
        Assert.Equal(5 * MiB, missing.Size);
        Assert.NotNull(state.PartUrlsExpireAt);
    }

    [Fact]
    public async Task State_returns_fresh_urls_for_the_missing_parts()
    {
        // Review Focus 2: a URL that ran out is replaced by the one GET signs now. Part URLs live 2 s in this factory.
        var creator = Guid.NewGuid();
        var client = shortLived.CreateClient();
        var upload = await client.CreateAsync(creator);

        await Task.Delay(TimeSpan.FromSeconds(4), Ct);

        using var old = await PutPartAsync(upload.PartUrls[0].Url, upload.PartUrls[0].Size);
        Assert.Equal(HttpStatusCode.Forbidden, old.StatusCode);

        // Refused because it ran out, not because of a wrong signature or host (ADR 0018: 403 AccessDenied, "Request has expired.").
        var body = await old.Content.ReadAsStringAsync(Ct);
        Assert.Contains("<Code>AccessDenied</Code>", body);
        Assert.Contains("expired", body, StringComparison.OrdinalIgnoreCase);

        var state = await client.GetAsync(creator, upload.Id);
        var fresh = state.PartUrls.Single(p => p.PartNumber == 1);
        using var again = await PutPartAsync(fresh.Url, fresh.Size);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task Complete_with_the_listed_etags_after_a_reload_succeeds()
    {
        // The browser lost its page: the ETags it sends come from GET only (ADR 0018).
        var creator = Guid.NewGuid();
        var client = Client;
        var upload = await client.CreateAsync(creator);
        foreach (var part in upload.PartUrls)
        {
            await PutPartOkAsync(part);
        }

        var state = await client.GetAsync(creator, upload.Id);
        var response = await client.PostCompleteAsync(creator, upload.Id, state.UploadedParts.Select(p => (p.PartNumber, p.ETag)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Processing", (await response.Content.ReadFromJsonAsync<UploadResponse>(Ct))!.Status);
    }

    [Fact]
    public async Task Complete_twice_creates_one_job()
    {
        // Review Focus 5: two parallel completes. Both answer 200, one moves the row, and only that one creates the job.
        // The race is forced: the test holds the row's lock, so both requests get past their checks and the S3 calls and then both
        // wait on the guarded UPDATE. When the lock is released one changes the row, and the other's WHERE no longer matches
        // (READ COMMITTED re-checks it against the new row version). Without the barrier the second request could start after the
        // first had finished and simply see Processing, and the guard would never be tested.
        var creator = Guid.NewGuid();
        var client = Client;
        var upload = await client.CreateAsync(creator);
        var etags = new List<(int, string)>();
        foreach (var part in upload.PartUrls)
        {
            etags.Add(await PutPartOkAsync(part));
        }

        await using var connection = new NpgsqlConnection(media.Settings["ConnectionStrings:Media"]);
        await connection.OpenAsync(Ct);
        var lockTransaction = await connection.BeginTransactionAsync(Ct);
        await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM uploads WHERE id = @id FOR UPDATE", connection, lockTransaction))
        {
            lockRow.Parameters.AddWithValue("id", upload.Id);
            await lockRow.ExecuteNonQueryAsync(Ct);
        }

        var both = Task.WhenAll(client.PostCompleteAsync(creator, upload.Id, etags), client.PostCompleteAsync(creator, upload.Id, etags));

        // Wait until both requests are blocked on the row. The count runs on a connection of its own, outside the lock's transaction:
        // PostgreSQL reads the list of sessions once per transaction, so inside it a session that connected after the first count
        // (the second request on a new pooled connection) is never listed.
        await using var observer = new NpgsqlConnection(media.Settings["ConnectionStrings:Media"]);
        await observer.OpenAsync(Ct);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var waiting = 0L;
        while (waiting < 2 && DateTime.UtcNow < deadline)
        {
            await using var count = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", observer);
            waiting = (long)(await count.ExecuteScalarAsync(Ct))!;
            await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);
        }

        Assert.Equal(2, waiting);
        await lockTransaction.RollbackAsync(Ct);
        var responses = await both;

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        foreach (var r in responses)
        {
            Assert.Equal("Processing", (await r.Content.ReadFromJsonAsync<UploadResponse>(Ct))!.Status);
        }

        var (status, jobs) = await media.WithDbAsync(async db => (
            (await db.Uploads.SingleAsync(u => u.Id == upload.Id, Ct)).Status,
            await db.ProcessingJobs.CountAsync(j => j.UploadId == upload.Id, Ct)));
        Assert.Equal(UploadStatus.Processing, status);
        Assert.Equal(1, jobs);
    }

    [Fact]
    public async Task Complete_after_s3_already_assembled_the_object_still_succeeds()
    {
        // A complete whose answer was lost: S3 assembled the object, Media's row still says Uploading. The retry meets NoSuchUpload,
        // finds the object with HeadObject and carries on.
        var creator = Guid.NewGuid();
        var client = Client;
        var upload = await client.CreateAsync(creator);
        var etags = new List<(int, string)>();
        foreach (var part in upload.PartUrls)
        {
            etags.Add(await PutPartOkAsync(part));
        }

        var s3UploadId = await UploadIdAsync(upload.Id);
        using var s3 = media.S3.CreateClient(media.S3.InternalUrl);
        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = media.Bucket,
            Key = ObjectKeys.Original(upload.VideoId, upload.Id),
            UploadId = s3UploadId,
            PartETags = [.. etags.Select(e => new PartETag(e.Item1, e.Item2))],
        }, Ct);

        // The premise: after a complete, Floci no longer knows the upload id (so Media's call below does meet NoSuchUpload).
        var again = await Assert.ThrowsAsync<AmazonS3Exception>(() => s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = media.Bucket,
            Key = ObjectKeys.Original(upload.VideoId, upload.Id),
            UploadId = s3UploadId,
            PartETags = [.. etags.Select(e => new PartETag(e.Item1, e.Item2))],
        }, Ct));
        Assert.Equal("NoSuchUpload", again.ErrorCode);

        var response = await client.PostCompleteAsync(creator, upload.Id, etags);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Processing", (await response.Content.ReadFromJsonAsync<UploadResponse>(Ct))!.Status);
        Assert.Equal(1, await media.WithDbAsync(db => db.ProcessingJobs.CountAsync(j => j.UploadId == upload.Id, Ct)));
    }

    [Fact]
    public async Task Complete_after_the_s3_upload_is_gone_is_409_upload_lost()
    {
        var creator = Guid.NewGuid();
        var client = Client;
        var video = NewVideo(creator);
        var upload = await client.CreateAsync(creator, videoId: video);
        var one = await PutPartOkAsync(upload.PartUrls[0]);
        await AbortOnS3Async(upload);

        var response = await client.PostCompleteAsync(creator, upload.Id, [one, (2, "\"x\""), (3, "\"x\"")]);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("urn:newstore:problem:upload-lost", (await response.ProblemAsync()).Type);

        // Complete is the creator's own request, so it writes the truth down: the row is Expired, so the video takes a new upload instead of staying stuck on this one.
        var row = await media.WithDbAsync(db => db.Uploads.SingleAsync(u => u.Id == upload.Id, Ct));
        Assert.Equal(UploadStatus.Expired, row.Status);
        Assert.Equal("upload-lost", row.FailureReason);
        Assert.NotNull(row.S3AbortedAt);
        Assert.Equal(HttpStatusCode.Created, (await client.PostCreateAsync(creator, video)).StatusCode);
    }

    private async Task AbortOnS3Async(UploadResponse upload)
    {
        using var s3 = media.S3.CreateClient(media.S3.InternalUrl);
        await s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
        {
            BucketName = media.Bucket,
            Key = ObjectKeys.Original(upload.VideoId, upload.Id),
            UploadId = await UploadIdAsync(upload.Id),
        }, Ct);
    }

    private Task<Upload> RowAsync(Guid id) => media.WithDbAsync(db => db.Uploads.AsNoTracking().SingleAsync(u => u.Id == id, Ct));

    [Fact]
    public async Task State_of_a_lost_upload_is_409_and_writes_nothing()
    {
        // S3 has forgotten the upload (a Floci restart, an abort by someone else, a lifecycle rule) and there is no object. GET says so
        // (409 upload-lost) but never writes: a complete may be running on S3 right now, and a GET that expired the row would lose it.
        var creator = Guid.NewGuid();
        var client = Client;
        var upload = await client.CreateAsync(creator);
        await PutPartOkAsync(upload.PartUrls[0]);
        await AbortOnS3Async(upload);
        var before = await RowAsync(upload.Id);

        var response = await client.SendAsUserAsync(creator, HttpMethod.Get, $"/uploads/{upload.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.ProblemAsync();
        Assert.Equal("urn:newstore:problem:upload-lost", problem.Type);
        Assert.Contains("delete", problem.Detail ?? "", StringComparison.OrdinalIgnoreCase);
        var after = await RowAsync(upload.Id);
        Assert.Equal(UploadStatus.Uploading, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.Version, after.Version);
    }

    [Fact]
    public async Task State_after_s3_assembled_the_object_reports_every_part_and_writes_nothing()
    {
        // A complete reached S3 and the row still says Uploading. GET reports every part as uploaded with no URLs left, so the client
        // calls complete, whose "already assembled" branch finishes the job. GET itself writes nothing.
        var creator = Guid.NewGuid();
        var client = Client;
        var upload = await client.CreateAsync(creator);
        var etags = new List<(int, string)>();
        foreach (var part in upload.PartUrls)
        {
            etags.Add(await PutPartOkAsync(part));
        }

        using var s3 = media.S3.CreateClient(media.S3.InternalUrl);
        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = media.Bucket,
            Key = ObjectKeys.Original(upload.VideoId, upload.Id),
            UploadId = await UploadIdAsync(upload.Id),
            PartETags = [.. etags.Select(e => new PartETag(e.Item1, e.Item2))],
        }, Ct);
        var before = await RowAsync(upload.Id);

        var state = await client.GetAsync(creator, upload.Id);

        Assert.Equal("Uploading", state.Status);
        Assert.Equal([1, 2, 3], state.UploadedParts.Select(p => p.PartNumber));
        Assert.Equal([5 * MiB, 5 * MiB, 2 * MiB], state.UploadedParts.Select(p => p.Size));
        Assert.Empty(state.PartUrls);
        var after = await RowAsync(upload.Id);
        Assert.Equal(UploadStatus.Uploading, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(0, await media.WithDbAsync(db => db.ProcessingJobs.CountAsync(j => j.UploadId == upload.Id, Ct)));

        // The client does what GET told it: complete with the parts it was given. That finishes the upload, and Media does not send
        // those placeholder ETags to S3: S3 sees exactly one CompleteMultipartUpload for this upload, the test's own above.
        var s3UploadId = await UploadIdAsync(upload.Id);
        Assert.Equal(1, media.Spy.CompletesFor(s3UploadId));
        var complete = await client.PostCompleteAsync(creator, upload.Id, state.UploadedParts.Select(p => (p.PartNumber, p.ETag)));
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Equal("Processing", (await complete.Content.ReadFromJsonAsync<UploadResponse>(Ct))!.Status);
        Assert.Equal(1, await media.WithDbAsync(db => db.ProcessingJobs.CountAsync(j => j.UploadId == upload.Id, Ct)));
        Assert.Equal(1, media.Spy.CompletesFor(s3UploadId));
    }

    [Fact]
    public async Task Complete_mixing_assembled_and_real_etags_is_400_invalid_parts()
    {
        var creator = Guid.NewGuid();
        var client = Client;
        var upload = await client.CreateAsync(creator);
        var etags = new List<(int, string)>();
        foreach (var part in upload.PartUrls)
        {
            etags.Add(await PutPartOkAsync(part));
        }

        using var s3 = media.S3.CreateClient(media.S3.InternalUrl);
        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = media.Bucket,
            Key = ObjectKeys.Original(upload.VideoId, upload.Id),
            UploadId = await UploadIdAsync(upload.Id),
            PartETags = [.. etags.Select(e => new PartETag(e.Item1, e.Item2))],
        }, Ct);
        var state = await client.GetAsync(creator, upload.Id);
        var mixed = state.UploadedParts.Select(p => (p.PartNumber, p.PartNumber == 1 ? etags[0].Item2 : p.ETag));

        var response = await client.PostCompleteAsync(creator, upload.Id, mixed);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("urn:newstore:problem:invalid-parts", (await response.ProblemAsync()).Type);
        Assert.Equal(UploadStatus.Uploading, (await RowAsync(upload.Id)).Status);
    }

    [Fact]
    public async Task Cancel_of_a_lost_upload_works()
    {
        // The way out of upload-lost: an abort that gets NoSuchUpload counts as confirmed, so the row ends as for any cancel.
        var creator = Guid.NewGuid();
        var client = Client;
        var video = NewVideo(creator);
        var upload = await client.CreateAsync(creator, videoId: video);
        await AbortOnS3Async(upload);

        var response = await client.SendAsUserAsync(creator, HttpMethod.Delete, $"/uploads/{upload.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var row = await RowAsync(upload.Id);
        Assert.Equal(UploadStatus.Expired, row.Status);
        Assert.Equal("cancelled", row.FailureReason);
        Assert.NotNull(row.S3AbortedAt);
        Assert.Equal(HttpStatusCode.Created, (await client.PostCreateAsync(creator, video)).StatusCode);
    }

    [Fact]
    public async Task Complete_with_a_missing_part_is_400()
    {
        var creator = Guid.NewGuid();
        var client = Client;
        var upload = await client.CreateAsync(creator);
        var one = await PutPartOkAsync(upload.PartUrls[0]);
        var two = await PutPartOkAsync(upload.PartUrls[1]);

        var response = await client.PostCompleteAsync(creator, upload.Id, [one, two, two]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var message = Assert.Single((await response.ProblemAsync()).Errors!["parts"]);
        Assert.Contains("missing parts: 3", message);
        Assert.Contains("repeated parts: 2", message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"parts\": null}")]
    [InlineData("{\"parts\": [null]}")]
    public async Task Complete_without_a_parts_list_is_400_not_500(string json)
    {
        var creator = Guid.NewGuid();
        var client = Client;
        var upload = await client.CreateAsync(creator);

        var response = await client.PostRawCompleteAsync(creator, upload.Id, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("parts", (await response.ProblemAsync()).Errors!.Keys);
    }

    [Fact]
    public async Task Complete_with_a_wrong_etag_is_400_invalid_parts()
    {
        var creator = Guid.NewGuid();
        var client = Client;
        var upload = await client.CreateAsync(creator);
        var etags = new List<(int, string)>();
        foreach (var part in upload.PartUrls)
        {
            etags.Add(await PutPartOkAsync(part));
        }

        etags[1] = (2, "\"00000000000000000000000000000000\"");
        var response = await client.PostCompleteAsync(creator, upload.Id, etags);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ProblemAsync();
        Assert.Equal("urn:newstore:problem:invalid-parts", problem.Type);
        // RFC 9457: the title is the same for every occurrence; what S3 said about this one is the detail.
        Assert.Equal("Storage refused the parts.", problem.Title);
        Assert.False(string.IsNullOrWhiteSpace(problem.Detail));
    }

    [Fact]
    public async Task Size_mismatch_after_complete_fails_the_upload()
    {
        // The part URLs sign their size, so a browser cannot send 11 MiB for a declared 12. The test uses S3 credentials
        // to put a shorter last part, which is what "a client could use another upload" (ADR 0018) looks like from Media.
        var creator = Guid.NewGuid();
        var client = Client;
        var upload = await client.CreateAsync(creator);
        var one = await PutPartOkAsync(upload.PartUrls[0]);
        var two = await PutPartOkAsync(upload.PartUrls[1]);
        using var s3 = media.S3.CreateClient(media.S3.InternalUrl);
        var key = ObjectKeys.Original(upload.VideoId, upload.Id);
        var shortPart = await s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = media.Bucket,
            Key = key,
            UploadId = await UploadIdAsync(upload.Id),
            PartNumber = 3,
            PartSize = 1 * MiB,
            InputStream = new MemoryStream(new byte[1 * MiB]),
        }, Ct);

        var response = await client.PostCompleteAsync(creator, upload.Id, [one, two, (3, shortPart.ETag)]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<UploadResponse>(Ct))!;
        Assert.Equal("Failed", body.Status);
        Assert.Equal("size-mismatch", body.FailureReason);
        Assert.Equal(0, await media.WithDbAsync(db => db.ProcessingJobs.CountAsync(j => j.UploadId == upload.Id, Ct)));
        var gone = await Assert.ThrowsAsync<AmazonS3Exception>(() => s3.GetObjectMetadataAsync(media.Bucket, key, Ct));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Size_mismatch_in_parallel_completes_answers_failed_to_both()
    {
        // Whichever request deletes the object first, the other must answer the same Failed row and not an error (a 404 on its HeadObject).
        // This does not force the race (the delete cannot be held between the two HeadObject calls without a seam in production code), so a
        // run may never hit it. That is acceptable: it cannot fail when the code is right, and it still checks the end state (both answer
        // Failed) when the requests do overlap.
        var creator = Guid.NewGuid();
        var client = Client;
        var upload = await client.CreateAsync(creator);
        var one = await PutPartOkAsync(upload.PartUrls[0]);
        var two = await PutPartOkAsync(upload.PartUrls[1]);
        using var s3 = media.S3.CreateClient(media.S3.InternalUrl);
        var shortPart = await s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = media.Bucket,
            Key = ObjectKeys.Original(upload.VideoId, upload.Id),
            UploadId = await UploadIdAsync(upload.Id),
            PartNumber = 3,
            PartSize = 1 * MiB,
            InputStream = new MemoryStream(new byte[1 * MiB]),
        }, Ct);

        var responses = await Task.WhenAll(
            client.PostCompleteAsync(creator, upload.Id, [one, two, (3, shortPart.ETag)]),
            client.PostCompleteAsync(creator, upload.Id, [one, two, (3, shortPart.ETag)]));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        foreach (var r in responses)
        {
            var body = (await r.Content.ReadFromJsonAsync<UploadResponse>(Ct))!;
            Assert.Equal("Failed", body.Status);
            Assert.Equal("size-mismatch", body.FailureReason);
        }
    }

    private Task<string> UploadIdAsync(Guid uploadId) =>
        media.WithDbAsync(async db => (await db.Uploads.SingleAsync(u => u.Id == uploadId, Ct)).S3UploadId);

    [Fact]
    public async Task Upload_over_2_GiB_is_refused()
    {
        var creator = Guid.NewGuid();

        var response = await Client.PostCreateAsync(creator, NewVideo(creator), size: (2L * 1024 * MiB) + 1);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("sizeBytes", (await response.ProblemAsync()).Errors!.Keys);
    }

    [Fact]
    public async Task Unsupported_type_is_refused()
    {
        var creator = Guid.NewGuid();

        var response = await Client.PostCreateAsync(creator, NewVideo(creator), fileName: "clip.avi", contentType: "video/x-msvideo");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("fileName", (await response.ProblemAsync()).Errors!.Keys);
    }

    [Fact]
    public async Task Someone_elses_video_is_403()
    {
        var response = await Client.PostCreateAsync(Guid.NewGuid(), NewVideo(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("urn:newstore:problem:not-your-video", (await response.ProblemAsync()).Type);
    }

    [Fact]
    public async Task Unknown_video_is_400()
    {
        var response = await Client.PostCreateAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("videoId", (await response.ProblemAsync()).Errors!.Keys);
    }

    [Fact]
    public async Task Published_video_is_409()
    {
        var creator = Guid.NewGuid();

        var response = await Client.PostCreateAsync(creator, NewVideo(creator, "Published"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("urn:newstore:problem:video-not-draft", (await response.ProblemAsync()).Type);
    }

    [Fact]
    public async Task Catalog_down_is_503_with_retry_after()
    {
        var creator = Guid.NewGuid();
        var video = NewVideo(creator);
        FakeCatalog.Break(video);

        var response = await Client.PostCreateAsync(creator, video);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("5", Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.Equal("urn:newstore:problem:dependency-unavailable", (await response.ProblemAsync()).Type);
    }

    [Fact]
    public async Task Second_upload_for_a_video_is_409_upload_exists()
    {
        var creator = Guid.NewGuid();
        var client = Client;
        var video = NewVideo(creator);
        var first = await client.CreateAsync(creator, videoId: video);

        var response = await client.PostCreateAsync(creator, video);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.ProblemAsync();
        Assert.Equal("urn:newstore:problem:upload-exists", problem.Type);
        Assert.Equal(first.Id, problem.UploadId);
    }

    [Fact]
    public async Task Parallel_creates_for_a_video_make_one_upload()
    {
        // Step 4 of POST /uploads: the unique index decides when both pass the "is there one?" check. The loser aborts its S3 upload.
        var creator = Guid.NewGuid();
        var client = Client;
        var video = NewVideo(creator);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.PostCreateAsync(creator, video)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await media.WithDbAsync(db => db.Uploads.CountAsync(u => u.VideoId == video, Ct)));
    }

    [Fact]
    public async Task Upload_after_a_failed_one_is_allowed()
    {
        var creator = Guid.NewGuid();
        var client = Client;
        var video = NewVideo(creator);
        var first = await client.CreateAsync(creator, videoId: video);
        await media.WithDbAsync(db => db.Uploads.Where(u => u.Id == first.Id).ExecuteUpdateAsync(
            s => s.SetProperty(u => u.Status, UploadStatus.Failed).SetProperty(u => u.FailureReason, "size-mismatch"), Ct));

        var second = await client.PostCreateAsync(creator, video);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
    }

    [Fact]
    public async Task Another_creators_upload_is_404()
    {
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var client = Client;
        var upload = await client.CreateAsync(owner);

        var get = await client.SendAsUserAsync(other, HttpMethod.Get, $"/uploads/{upload.Id}");
        var complete = await client.PostCompleteAsync(other, upload.Id, [(1, "\"x\""), (2, "\"x\""), (3, "\"x\"")]);
        var delete = await client.SendAsUserAsync(other, HttpMethod.Delete, $"/uploads/{upload.Id}");

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, complete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal("Uploading", (await client.GetAsync(owner, upload.Id)).Status);
    }

    [Fact]
    public async Task Cancel_aborts_the_s3_upload_and_expires_it()
    {
        var creator = Guid.NewGuid();
        var client = Client;
        var upload = await client.CreateAsync(creator);
        var s3UploadId = await UploadIdAsync(upload.Id);
        await PutPartOkAsync(upload.PartUrls[0]);

        var response = await client.SendAsUserAsync(creator, HttpMethod.Delete, $"/uploads/{upload.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var row = await media.WithDbAsync(db => db.Uploads.SingleAsync(u => u.Id == upload.Id, Ct));
        Assert.Equal(UploadStatus.Expired, row.Status);
        Assert.Equal("cancelled", row.FailureReason);
        Assert.NotNull(row.S3AbortedAt);
        using var s3 = media.S3.CreateClient(media.S3.InternalUrl);
        var gone = await Assert.ThrowsAsync<AmazonS3Exception>(() => s3.ListPartsAsync(
            new ListPartsRequest { BucketName = media.Bucket, Key = row.ObjectKey, UploadId = s3UploadId }, Ct));
        Assert.Equal("NoSuchUpload", gone.ErrorCode);

        var again = await client.SendAsUserAsync(creator, HttpMethod.Delete, $"/uploads/{upload.Id}");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("urn:newstore:problem:not-uploading", (await again.ProblemAsync()).Type);
    }

    [Fact]
    public async Task Upload_endpoints_need_a_signed_in_user()
    {
        var client = Client;
        var id = Guid.NewGuid();

        var anonymous = await client.PostAsJsonAsync("/uploads", new { }, Ct);
        var service = new HttpRequestMessage(HttpMethod.Get, $"/uploads/{id}");
        service.Headers.Authorization = new("Bearer", TestTokens.Client("catalog", "media"));
        var asService = await client.SendAsync(service, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asService.StatusCode);
    }

    [Fact]
    public async Task My_uploads_lists_the_latest_per_video()
    {
        var creator = Guid.NewGuid();
        var client = Client;
        var videoA = NewVideo(creator);
        var videoB = NewVideo(creator);
        var firstA = await client.CreateAsync(creator, videoId: videoA);
        media.Clock.Advance(TimeSpan.FromSeconds(1));
        var onlyB = await client.CreateAsync(creator, videoId: videoB);
        media.Clock.Advance(TimeSpan.FromSeconds(1));
        await client.SendAsUserAsync(creator, HttpMethod.Delete, $"/uploads/{firstA.Id}");
        var secondA = await client.CreateAsync(creator, videoId: videoA);

        var list = await (await client.SendAsUserAsync(creator, HttpMethod.Get, "/me/uploads")).Content.ReadFromJsonAsync<List<UploadSummary>>(Ct);

        Assert.Equal([secondA.Id, onlyB.Id], list!.Select(u => u.Id));
        Assert.Equal(["Uploading", "Uploading"], list!.Select(u => u.Status));
        Assert.DoesNotContain(list!, u => u.Id == firstA.Id);
    }
}
