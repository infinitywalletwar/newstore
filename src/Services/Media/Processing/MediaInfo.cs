namespace NewStore.Media.Processing;

/// <summary>
/// What ffprobe found in an uploaded file. <paramref name="FormatName"/> is the demuxer, for example
/// <c>mov,mp4,m4a,3gp,3g2,mj2</c>; the size and frame rate are those of the first video stream.
/// </summary>
public sealed record MediaInfo(string FormatName, double DurationSeconds, bool HasAudio, int Width, int Height, double FramesPerSecond);
