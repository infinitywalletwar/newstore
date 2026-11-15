using NewStore.Messaging;

namespace NewStore.Contracts.Media;

/// <summary>
/// Media finished processing an upload: the video can be played. Catalog turns its draft into a published video
/// (ADR 0019). Media is the only producer and Catalog the only consumer.
/// </summary>
public sealed record VideoPublished : IIntegrationEvent
{
    /// <summary>The topic. The key is <see cref="VideoId"/>.</summary>
    public const string Topic = "media.videos";

    /// <summary>Both the producer and the consumer declare the topic with this count, so either may create it.</summary>
    public const int Partitions = 3;

    public required Guid EventId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid VideoId { get; init; }

    /// <summary>The upload that produced the files.</summary>
    public required Guid UploadId { get; init; }

    /// <summary><c>hls/{videoId}/{uploadId}/master.m3u8</c>, relative to the public media base URL.</summary>
    public required string PlaylistPath { get; init; }

    public required string ThumbnailPath { get; init; }

    public required int DurationSeconds { get; init; }
}
