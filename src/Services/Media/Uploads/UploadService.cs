using System.Globalization;

using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Npgsql;

namespace NewStore.Media;

/// <summary>
/// Everything about an upload's life that touches the database or S3. The endpoints only translate its answers to HTTP.
/// The bytes never come through here: the browser sends them to S3 through presigned part URLs.
/// </summary>
public sealed partial class UploadService(
    MediaDbContext db, MediaStorage storage, IOptions<UploadOptions> options, TimeProvider time, ILogger<UploadService> logger)
{
    /// <summary>
    /// The ETag <c>GET</c> reports for every part once S3 has assembled the object (S3 no longer lists parts, so it has no real ones).
    /// Complete recognises it and does not send it to S3.
    /// </summary>
    public const string AssembledETag = "\"assembled\"";

    private const string LiveVideoIndex = "ix_uploads_live_video";

    private static readonly UploadStatus[] LiveStatuses = [UploadStatus.Uploading, UploadStatus.Processing, UploadStatus.Published];

    private UploadOptions Options => options.Value;

    public static int PartCountFor(long sizeBytes, long partSize) => (int)((sizeBytes + partSize - 1) / partSize);

    /// <summary>The size of part <paramref name="partNumber"/>: every part but the last is <c>PartSize</c>, the last is what remains.</summary>
    public static long PartSizeOf(Upload upload, int partNumber) =>
        partNumber < upload.PartCount ? upload.PartSize : upload.SizeBytes - ((upload.PartCount - 1) * upload.PartSize);

    public async Task<CreateOutcome> CreateAsync(Guid creatorId, CreateUploadRequest request, CancellationToken ct)
    {
        if (await LiveUploadIdAsync(request.VideoId, ct) is { } existing)
        {
            return new CreateOutcome.Exists(existing);
        }

        var id = Guid.CreateVersion7();
        var key = ObjectKeys.Original(request.VideoId, id);
        InitiateMultipartUploadResponse started;
        try
        {
            started = await storage.Client.InitiateMultipartUploadAsync(
                new InitiateMultipartUploadRequest { BucketName = storage.Bucket, Key = key, ContentType = request.ContentType }, ct);
        }
        catch (Exception ex) when (IsStorageDown(ex))
        {
            return new CreateOutcome.StorageUnavailable();
        }

        var now = time.GetUtcNowToMicrosecond();
        var upload = new Upload
        {
            Id = id,
            VideoId = request.VideoId,
            CreatorId = creatorId,
            FileName = request.FileName,
            ContentType = request.ContentType,
            SizeBytes = request.SizeBytes,
            ObjectKey = key,
            S3UploadId = started.UploadId,
            PartSize = Options.PartSize,
            PartCount = PartCountFor(request.SizeBytes, Options.PartSize),
            Status = UploadStatus.Uploading,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Uploads.Add(upload);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: LiveVideoIndex })
        {
            // A parallel create won the race between our check and our insert. Give back the S3 upload we just made.
            // CancellationToken.None: the request may be the thing that was cancelled, and the upload must not leak.
            db.Entry(upload).State = EntityState.Detached;
            await TryAbortAsync(key, started.UploadId, CancellationToken.None);
            return new CreateOutcome.Exists(await LiveUploadIdAsync(request.VideoId, ct) ?? Guid.Empty);
        }

        return new CreateOutcome.Created(upload);
    }

    private Task<Guid?> LiveUploadIdAsync(Guid videoId, CancellationToken ct) =>
        db.Uploads.AsNoTracking()
            .Where(u => u.VideoId == videoId && LiveStatuses.Contains(u.Status))
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>The caller's upload, or null when there is none or it is another creator's (a 404 either way).</summary>
    public Task<Upload?> FindAsync(Guid creatorId, Guid id, CancellationToken ct) =>
        db.Uploads.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id && u.CreatorId == creatorId, ct);

    /// <summary>
    /// The upload as the API shows it. While <c>Uploading</c> it asks S3 which parts it already has (<c>ListParts</c>) and signs
    /// fresh URLs for the others, so a browser that reloaded the page needs only this call to carry on. It never writes. If S3 no
    /// longer has the upload it answers from what S3 shows: the object exists (every part is reported as uploaded) or it is lost.
    /// </summary>
    public async Task<StateOutcome> GetStateAsync(Guid creatorId, Guid id, CancellationToken ct)
    {
        var upload = await FindAsync(creatorId, id, ct);
        if (upload is null)
        {
            return new StateOutcome.NotFound();
        }

        if (upload.Status != UploadStatus.Uploading)
        {
            return new StateOutcome.Ok(await ResponseAsync(upload, [], ct));
        }

        try
        {
            return new StateOutcome.Ok(await ResponseAsync(upload, await ListPartsAsync(upload, ct), ct));
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchUpload")
        {
            // GET never writes (see the walkthrough): a complete may be running on S3 right now, and a GET that expired the row would
            // lose that upload. It only reports what it sees.
            try
            {
                if (await ObjectSizeAsync(upload.ObjectKey, ct) is null)
                {
                    return new StateOutcome.Lost();
                }
            }
            catch (Exception e) when (IsStorageDown(e))
            {
                return new StateOutcome.StorageUnavailable();
            }

            // The object exists: S3 has assembled the parts. Say every part is uploaded, so the client calls complete, whose "already
            // assembled" branch finishes the job. The ETags are the placeholder <see cref="AssembledETag"/>: S3 no longer lists parts, and complete does not send it to S3.
            UploadedPart[] all =
            [
                .. Enumerable.Range(1, upload.PartCount).Select(n => new UploadedPart(n, AssembledETag, PartSizeOf(upload, n))),
            ];
            return new StateOutcome.Ok(await ResponseAsync(upload, all, ct));
        }
        catch (Exception ex) when (IsStorageDown(ex))
        {
            return new StateOutcome.StorageUnavailable();
        }
    }

    /// <summary>
    /// The response for <paramref name="upload"/>, given the parts S3 has. While <c>Uploading</c> it signs a URL for every part that is
    /// not in <paramref name="uploaded"/>; in any other status there is nothing to send.
    /// </summary>
    public async Task<UploadResponse> ResponseAsync(Upload upload, IReadOnlyList<UploadedPart> uploaded, CancellationToken ct)
    {
        PartUrl[] urls = [];
        DateTimeOffset? urlsExpireAt = null;

        if (upload.Status == UploadStatus.Uploading)
        {
            var have = uploaded.Select(p => p.PartNumber).ToHashSet();
            var missing = Enumerable.Range(1, upload.PartCount).Where(n => !have.Contains(n));
            urls = await Task.WhenAll(missing.Select(async n => new PartUrl(
                n,
                await storage.PresignPartAsync(upload.ObjectKey, upload.S3UploadId, n, PartSizeOf(upload, n), Options.PartUrlLifetime),
                PartSizeOf(upload, n))));
            urlsExpireAt = urls.Length > 0 ? time.GetUtcNowToMicrosecond() + Options.PartUrlLifetime : null;
        }
        else
        {
            uploaded = [];
        }

        return new UploadResponse(
            upload.Id, upload.VideoId, upload.FileName, upload.SizeBytes, upload.Status.ToString(), upload.FailureReason,
            upload.PartSize, upload.PartCount, uploaded, urls, urlsExpireAt, upload.CreatedAt, upload.UpdatedAt);
    }

    /// <summary>All uploaded parts, page by page: S3 returns at most 1,000 parts per call.</summary>
    private async Task<List<UploadedPart>> ListPartsAsync(Upload upload, CancellationToken ct)
    {
        var parts = new List<UploadedPart>();
        int? marker = null;
        while (true)
        {
            var page = await storage.Client.ListPartsAsync(new ListPartsRequest
            {
                BucketName = storage.Bucket,
                Key = upload.ObjectKey,
                UploadId = upload.S3UploadId,
                MaxParts = 1000,
                PartNumberMarker = marker?.ToString(CultureInfo.InvariantCulture),
            }, ct);

            parts.AddRange((page.Parts ?? []).Select(p => new UploadedPart(p.PartNumber ?? 0, p.ETag, p.Size ?? 0)));
            if (page.IsTruncated != true)
            {
                return parts;
            }

            marker = page.NextPartNumberMarker;
        }
    }

    public async Task<CompleteOutcome> CompleteAsync(Guid creatorId, Guid id, CompleteUploadRequest request, CancellationToken ct)
    {
        var upload = await FindAsync(creatorId, id, ct);
        if (upload is null)
        {
            return new CompleteOutcome.NotFound();
        }

        switch (upload.Status)
        {
            case UploadStatus.Expired:
                return ToOutcome(upload);
            case not UploadStatus.Uploading:
                // Processing, Published or Failed: the earlier complete (or the failure) already decided. Say so, change nothing.
                return new CompleteOutcome.Done(upload);
        }

        if (CheckParts(upload, request.Parts) is { } badParts)
        {
            return new CompleteOutcome.BadParts(badParts);
        }

        try
        {
            // The parts GET reported for an assembled object carry the placeholder ETag. Real S3 may answer InvalidPart to a made-up
            // ETag, so complete never sends them: all placeholders means "S3 is done, finish"; a mix is a client bug.
            var assembled = request.Parts.Count(p => p.ETag == AssembledETag);
            if (assembled == request.Parts.Count)
            {
                return ToOutcome(await ReconcileLostUploadAsync(upload, ct));
            }

            if (assembled > 0)
            {
                return new CompleteOutcome.InvalidParts("Parts reported as assembled cannot be mixed with parts that carry their own ETag.");
            }

            try
            {
                await storage.Client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
                {
                    BucketName = storage.Bucket,
                    Key = upload.ObjectKey,
                    UploadId = upload.S3UploadId,
                    PartETags = [.. request.Parts.OrderBy(p => p.PartNumber).Select(p => new PartETag(p.PartNumber, p.ETag))],
                }, ct);
            }
            catch (AmazonS3Exception ex) when (ex.ErrorCode is "InvalidPart" or "InvalidPartOrder" or "EntityTooSmall")
            {
                return new CompleteOutcome.InvalidParts(ex.Message);
            }
            catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchUpload")
            {
                // S3 no longer knows the upload id: an earlier complete worked (a retry, or a parallel request), or it is gone for good.
                return ToOutcome(await ReconcileLostUploadAsync(upload, ct));
            }

            if (await ObjectSizeAsync(upload.ObjectKey, ct) is not { } size)
            {
                // Complete said yes and the object is not there: treat it as lost, like the NoSuchUpload case.
                return ToOutcome(await ReconcileLostUploadAsync(upload, ct));
            }

            return ToOutcome(await FinishAsync(upload, size, ct));
        }
        catch (Exception ex) when (IsStorageDown(ex))
        {
            return new CompleteOutcome.StorageUnavailable();
        }
    }

    private static CompleteOutcome ToOutcome(Upload upload) => upload switch
    {
        { Status: UploadStatus.Expired, FailureReason: "upload-lost" } => new CompleteOutcome.Lost(),
        { Status: UploadStatus.Expired } => new CompleteOutcome.Expired(),
        _ => new CompleteOutcome.Done(upload),
    };

    /// <summary>The size of the stored object, or null when there is none (HeadObject answers 404).</summary>
    private async Task<long?> ObjectSizeAsync(string key, CancellationToken ct)
    {
        try
        {
            return (await storage.Client.GetObjectMetadataAsync(storage.Bucket, key, ct)).ContentLength;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// The object exists, so S3 finished the upload. A size other than the declared one fails the upload (and the object is deleted,
    /// after the row says so: a leftover private original costs less than a wrong status). Otherwise the guarded move to Processing.
    /// Returns the row as it is afterwards, whoever changed it.
    /// </summary>
    private async Task<Upload> FinishAsync(Upload upload, long size, CancellationToken ct)
    {
        if (size == upload.SizeBytes)
        {
            return await MoveToProcessingAsync(upload.Id, ct);
        }

        // The creator declared one size and sent another (or used another upload's parts). Nothing will process this.
        await FailAsync(upload.Id, "size-mismatch", ct);
        try
        {
            await storage.Client.DeleteObjectAsync(storage.Bucket, upload.ObjectKey, ct);
        }
        catch (Exception ex) when (IsStorageDown(ex))
        {
            DeleteFailed(logger, ex, upload.ObjectKey);
        }

        return await ReloadAsync(upload.Id, ct);
    }

    /// <summary>
    /// Complete's own request met NoSuchUpload for an <c>Uploading</c> row, so the row is behind the truth (GET does not do this: it never writes). If the object exists, an earlier complete
    /// reached S3 and its answer was lost: finish as complete would. Otherwise the upload is lost (a storage restart, an abort by
    /// someone else, a lifecycle rule) and the row becomes <c>Expired</c> with <c>upload-lost</c>, so the video takes a new upload.
    /// Every write is guarded by <c>status = 'Uploading'</c>; the answer is the row as it is afterwards.
    /// </summary>
    private async Task<Upload> ReconcileLostUploadAsync(Upload upload, CancellationToken ct)
    {
        if (await ObjectSizeAsync(upload.ObjectKey, ct) is { } size)
        {
            return await FinishAsync(upload, size, ct);
        }

        // Nothing is left on S3, so there is nothing to abort: S3AbortedAt is set too.
        var now = time.GetUtcNowToMicrosecond();
        await db.Uploads
            .Where(u => u.Id == upload.Id && u.Status == UploadStatus.Uploading)
            .ExecuteUpdateAsync(set => set
                .SetProperty(u => u.Status, UploadStatus.Expired)
                .SetProperty(u => u.FailureReason, "upload-lost")
                .SetProperty(u => u.S3AbortedAt, now)
                .SetProperty(u => u.UpdatedAt, now), ct);
        return await ReloadAsync(upload.Id, ct);
    }

    /// <summary>Null when the parts are exactly 1 to PartCount, each once; otherwise a message naming what is wrong.</summary>
    private static string? CheckParts(Upload upload, IReadOnlyList<CompletedPart>? parts)
    {
        // System.Text.Json does not enforce nullable annotations: {} and {"parts": null} arrive as null, {"parts": [null]} as a null item.
        if (parts is null || parts.Any(p => p is null))
        {
            return "parts must be a list of { partNumber, eTag }.";
        }

        var numbers = parts.Select(p => p.PartNumber).ToList();
        var repeated = numbers.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).Order().ToList();
        var missing = Enumerable.Range(1, upload.PartCount).Except(numbers).ToList();
        var unknown = numbers.Where(n => n < 1 || n > upload.PartCount).Distinct().Order().ToList();
        var noETag = parts.Where(p => string.IsNullOrWhiteSpace(p.ETag)).Select(p => p.PartNumber).Order().ToList();

        var problems = new List<string>();
        if (missing.Count > 0)
        {
            problems.Add($"missing parts: {string.Join(", ", missing)}");
        }

        if (repeated.Count > 0)
        {
            problems.Add($"repeated parts: {string.Join(", ", repeated)}");
        }

        if (unknown.Count > 0)
        {
            problems.Add($"parts outside 1 to {upload.PartCount}: {string.Join(", ", unknown)}");
        }

        if (noETag.Count > 0)
        {
            problems.Add($"parts without an ETag: {string.Join(", ", noETag)}");
        }

        return problems.Count == 0 ? null : $"The parts must be 1 to {upload.PartCount}, each once ({string.Join("; ", problems)}).";
    }

    /// <summary>
    /// Uploading to Processing and the job, in one transaction. The UPDATE is guarded by <c>status = 'Uploading'</c>, so of two
    /// parallel completes only one changes a row and creates the job; the other reloads and answers the current state.
    /// </summary>
    private async Task<Upload> MoveToProcessingAsync(Guid id, CancellationToken ct)
    {
        var now = time.GetUtcNowToMicrosecond();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var moved = await db.Uploads
            .Where(u => u.Id == id && u.Status == UploadStatus.Uploading)
            .ExecuteUpdateAsync(set => set
                .SetProperty(u => u.Status, UploadStatus.Processing)
                .SetProperty(u => u.CompletedAt, now)
                .SetProperty(u => u.UpdatedAt, now), ct);
        if (moved == 1)
        {
            db.ProcessingJobs.Add(new ProcessingJob
            {
                Id = Guid.CreateVersion7(),
                UploadId = id,
                Status = JobStatus.Pending,
                NextRunAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        return await ReloadAsync(id, ct);
    }

    private async Task FailAsync(Guid id, string reason, CancellationToken ct)
    {
        var now = time.GetUtcNowToMicrosecond();
        await db.Uploads
            .Where(u => u.Id == id && u.Status == UploadStatus.Uploading)
            .ExecuteUpdateAsync(set => set
                .SetProperty(u => u.Status, UploadStatus.Failed)
                .SetProperty(u => u.FailureReason, reason)
                .SetProperty(u => u.UpdatedAt, now), ct);
    }

    private Task<Upload> ReloadAsync(Guid id, CancellationToken ct) =>
        db.Uploads.AsNoTracking().SingleAsync(u => u.Id == id, ct);

    public async Task<CancelOutcome> CancelAsync(Guid creatorId, Guid id, CancellationToken ct)
    {
        var upload = await FindAsync(creatorId, id, ct);
        if (upload is null)
        {
            return CancelOutcome.NotFound;
        }

        var now = time.GetUtcNowToMicrosecond();
        var moved = await db.Uploads
            .Where(u => u.Id == id && u.Status == UploadStatus.Uploading)
            .ExecuteUpdateAsync(set => set
                .SetProperty(u => u.Status, UploadStatus.Expired)
                .SetProperty(u => u.FailureReason, "cancelled")
                .SetProperty(u => u.UpdatedAt, now), ct);
        if (moved == 0)
        {
            return CancelOutcome.NotUploading;
        }

        // The row says Expired now. If S3 cannot be reached, S3AbortedAt stays null and the expiry service retries the abort.
        if (await TryAbortAsync(upload.ObjectKey, upload.S3UploadId, ct))
        {
            await MarkAbortedAsync(id, ct);
        }

        return CancelOutcome.Done;
    }

    /// <summary>
    /// The expiry pass: moves up to 100 abandoned <c>Uploading</c> rows to <c>Expired</c> first, so a complete that races it sees
    /// Expired; then aborts the S3 upload of every Expired row that has not been aborted yet (these and earlier failures).
    /// Returns how many rows it expired.
    /// </summary>
    public async Task<int> ExpireAbandonedAsync(CancellationToken ct)
    {
        var now = time.GetUtcNowToMicrosecond();
        var cutoff = now - Options.AbandonAfter;
        var expired = await db.Database.ExecuteSqlAsync($"""
            UPDATE uploads SET status = 'Expired', failure_reason = 'abandoned', updated_at = {now}
            WHERE status = 'Uploading'
              AND id IN (SELECT id FROM uploads WHERE status = 'Uploading' AND created_at < {cutoff}
                         ORDER BY created_at LIMIT 100 FOR UPDATE SKIP LOCKED)
            """, ct);

        var toAbort = await db.Uploads.AsNoTracking()
            .Where(u => u.Status == UploadStatus.Expired && u.S3AbortedAt == null)
            .OrderBy(u => u.UpdatedAt)
            .Take(100)
            .Select(u => new { u.Id, u.ObjectKey, u.S3UploadId })
            .ToListAsync(ct);

        foreach (var upload in toAbort)
        {
            if (await TryAbortAsync(upload.ObjectKey, upload.S3UploadId, ct))
            {
                await MarkAbortedAsync(upload.Id, ct);
            }
        }

        return expired;
    }

    private Task<int> MarkAbortedAsync(Guid id, CancellationToken ct)
    {
        var now = time.GetUtcNowToMicrosecond();
        return db.Uploads.Where(u => u.Id == id).ExecuteUpdateAsync(set => set.SetProperty(u => u.S3AbortedAt, now), ct);
    }

    /// <summary>Aborts the S3 upload. <c>NoSuchUpload</c> counts as done. False when S3 could not be reached or refused.</summary>
    private async Task<bool> TryAbortAsync(string key, string s3UploadId, CancellationToken ct)
    {
        try
        {
            await storage.Client.AbortMultipartUploadAsync(
                new AbortMultipartUploadRequest { BucketName = storage.Bucket, Key = key, UploadId = s3UploadId }, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchUpload")
        {
            return true;
        }
        catch (Exception ex) when (ex is AmazonServiceException or HttpRequestException)
        {
            AbortFailed(logger, ex, key);
            return false;
        }
    }

    /// <summary>S3 could not be reached or answered 5xx. Other S3 errors (a 403, a bad request) are bugs and are not hidden as a 503.</summary>
    private static bool IsStorageDown(Exception ex) =>
        ex is HttpRequestException
        || (ex is AmazonServiceException service
            && ((int)service.StatusCode >= 500 || service.InnerException is HttpRequestException or IOException));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the wrong-sized object {Key}; the row says Failed.")]
    private static partial void DeleteFailed(ILogger logger, Exception exception, string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not abort the S3 upload of {Key}; the expiry service tries again.")]
    private static partial void AbortFailed(ILogger logger, Exception exception, string key);

    /// <summary>
    /// The creator's newest upload of each video, newest first. <c>DISTINCT ON</c> is PostgreSQL's "first row of each group": EF Core
    /// cannot translate the LINQ form (<c>GroupBy</c> then <c>First</c> of an ordered group), which threw when this was tried.
    /// <c>*</c> does not include the system column <c>xmin</c>, which maps <see cref="Upload.Version"/>, so it is listed. The ids are version 7, so they break a tie between two uploads created in the same microsecond in creation order.
    /// </summary>
    public async Task<List<Upload>> LatestPerVideoAsync(Guid creatorId, CancellationToken ct) =>
        await db.Uploads
            .FromSql($"""
                SELECT DISTINCT ON (video_id) *, xmin FROM uploads
                WHERE creator_id = {creatorId}
                ORDER BY video_id, created_at DESC, id DESC
                """)
            .AsNoTracking()
            .OrderByDescending(u => u.CreatedAt)
            .ThenByDescending(u => u.Id)
            .ToListAsync(ct);
}
