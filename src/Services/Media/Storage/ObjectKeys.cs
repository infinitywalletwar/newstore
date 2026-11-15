namespace NewStore.Media;

/// <summary>Where things live in the bucket (plan, "Storage layout").</summary>
public static class ObjectKeys
{
    public const string SampleOriginal = "originals/sample.mp4";
    public const string SampleHlsPrefix = "hls/sample";
    public const string Master = "master.m3u8";
    public const string Thumbnail = "thumbnail.jpg";

    /// <summary>An original is private: it is read only through a presigned URL.</summary>
    public static string Original(Guid videoId, Guid uploadId) => $"originals/{videoId}/{uploadId}";

    /// <summary>Everything the worker makes for an upload goes under this prefix, which the bucket policy makes public.</summary>
    public static string HlsPrefix(Guid videoId, Guid uploadId) => $"hls/{videoId}/{uploadId}";
}
