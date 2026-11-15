using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

using NewStore.Contracts.Media;
using NewStore.Messaging;

namespace NewStore.Catalog.Videos;

/// <summary>
/// Turns a draft into a published video when Media says its files are ready (ADR 0019). One UPDATE, guarded by the
/// status: it changes a draft and nothing else, so a second <see cref="VideoPublished"/> for the same video (a new
/// message id, which the inbox lets through) cannot move a published video to other files or another time.
/// </summary>
public sealed partial class VideoPublishedHandler(
    CatalogDbContext db, HybridCache cache, ILogger<VideoPublishedHandler> logger)
    : IIntegrationEventHandler<VideoPublished>
{
    public async Task HandleAsync(VideoPublished integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        // Runs in the transaction the dispatcher opened on this context, so it commits with the inbox row.
        // The interpolated values become parameters, not text in the SQL. The status strings are what the
        // enum's string conversion stores (see CatalogDbContext).
        var changed = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE videos
            SET status = 'Published',
                published_at = {integrationEvent.OccurredAt.ToUniversalTime()},
                duration_seconds = {integrationEvent.DurationSeconds},
                playlist_path = {integrationEvent.PlaylistPath},
                thumbnail_path = {integrationEvent.ThumbnailPath}
            WHERE id = {integrationEvent.VideoId} AND status = 'Draft'
            """,
            cancellationToken);

        if (changed == 0)
        {
            if (!await db.Videos.AnyAsync(v => v.Id == integrationEvent.VideoId, cancellationToken))
            {
                // Catalog creates every video before Media can accept an upload for it, so this is a bug or a wrong
                // topic, not a race: retrying cannot help, and the dead-letter topic keeps the message.
                throw new InvalidOperationException($"VideoPublished names video {integrationEvent.VideoId}, which Catalog does not have.");
            }

            AlreadyPublished(logger, integrationEvent.VideoId, integrationEvent.UploadId);
            return;
        }

        // The video page and its channel's pages may hold the draft's answer (a 404 is removed at once, a channel
        // page is not). Tags drop them; one cached in the race between this and the commit expires within 60 seconds (ADR 0009).
        var slug = await db.Videos
            .Where(v => v.Id == integrationEvent.VideoId)
            .Join(db.Channels, v => v.ChannelId, c => c.Id, (v, c) => c.Slug)
            .SingleAsync(cancellationToken);
        await cache.RemoveByTagAsync([$"video:{integrationEvent.VideoId}", $"channel:{slug}"], cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignored VideoPublished for video {VideoId} (upload {UploadId}): the video is already published.")]
    private static partial void AlreadyPublished(ILogger logger, Guid videoId, Guid uploadId);
}
