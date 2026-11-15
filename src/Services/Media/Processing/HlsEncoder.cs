using System.Globalization;

using Microsoft.Extensions.Options;

using NewStore.Media.Storage;

namespace NewStore.Media.Processing;

/// <summary>
/// Turns a probed source into HLS at 360p and 720p (<c>master.m3u8</c>, <c>v0/</c>, <c>v1/</c>) and a thumbnail, with the plan's
/// commands (Global Constraints, "The FFmpeg commands").
/// </summary>
public sealed class HlsEncoder(FfmpegRunner runner, IOptions<ProcessingOptions> options)
{
    /// <summary>The target segment length. FFmpeg cuts a segment only at a key frame, so the key frames come every 6 s too.</summary>
    public const int SegmentSeconds = 6;

    /// <exception cref="PermanentProcessingException"><c>encode-failed</c>, with FFmpeg's last stderr lines.</exception>
    public async Task EncodeAsync(string source, MediaInfo info, string outputDirectory, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(outputDirectory, "v0"));
        Directory.CreateDirectory(Path.Combine(outputDirectory, "v1"));
        await RunAsync(EncodeArguments(source, info, outputDirectory, options.Value.X264Preset), ct);
    }

    /// <summary>One frame, 360 pixels high, from 5 s in (or the middle of a shorter video).</summary>
    /// <exception cref="PermanentProcessingException"><c>encode-failed</c>, with FFmpeg's last stderr lines.</exception>
    public Task ThumbnailAsync(string source, MediaInfo info, string outputDirectory, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDirectory);
        return RunAsync(ThumbnailArguments(source, info, outputDirectory), ct);
    }

    /// <summary>The thumbnail's command, one argument per entry.</summary>
    public static IReadOnlyList<string> ThumbnailArguments(string source, MediaInfo info, string outputDirectory)
    {
        var at = Math.Min(5, info.DurationSeconds / 2).ToString("0.###", CultureInfo.InvariantCulture);
        return
        [
            "-nostdin", "-y", "-protocol_whitelist", "file", "-format_whitelist", "mov,matroska",
            "-ss", at, "-i", source, "-frames:v", "1", "-vf", "scale=-2:360", Path.Combine(outputDirectory, "thumbnail.jpg"),
        ];
    }

    /// <summary>
    /// The encode, one argument per entry. <c>scale=-2:h</c> keeps the aspect ratio with an even width; audio is mapped only when
    /// ffprobe found it, because a map to a missing stream fails the run.
    /// </summary>
    public static IReadOnlyList<string> EncodeArguments(string source, MediaInfo info, string outputDirectory, string preset)
    {
        // topic 03's rule: -g is hls_time times the frame rate, so every segment starts on a key frame.
        var gop = ((int)Math.Round(SegmentSeconds * info.FramesPerSecond, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
        List<string> arguments = ["-nostdin", "-y", "-protocol_whitelist", "file", "-format_whitelist", "mov,matroska", "-i", source];
        arguments.AddRange(info.HasAudio
            ? ["-map", "0:v:0", "-map", "0:a:0", "-map", "0:v:0", "-map", "0:a:0"]
            : ["-map", "0:v:0", "-map", "0:v:0"]);
        arguments.AddRange(
        [
            "-filter:v:0", "scale=-2:360", "-b:v:0", "800k", "-filter:v:1", "scale=-2:720", "-b:v:1", "2800k",
            "-c:v", "libx264", "-preset", preset, "-pix_fmt", "yuv420p",
            "-g", gop, "-keyint_min", gop, "-sc_threshold", "0",
        ]);
        if (info.HasAudio)
        {
            arguments.AddRange(["-c:a", "aac", "-b:a", "128k", "-ac", "2"]);
        }

        arguments.AddRange(
        [
            "-f", "hls", "-hls_time", SegmentSeconds.ToString(CultureInfo.InvariantCulture), "-hls_playlist_type", "vod",
            "-hls_flags", "independent_segments", "-master_pl_name", "master.m3u8",
            "-var_stream_map", info.HasAudio ? "v:0,a:0 v:1,a:1" : "v:0 v:1",
            "-hls_segment_filename", Path.Combine(outputDirectory, "v%v", "seg_%03d.ts"), Path.Combine(outputDirectory, "v%v", "index.m3u8"),
        ]);
        return arguments;
    }

    /// <summary>Segments never change once published, so caches may keep them for a year.</summary>
    public const string SegmentCacheControl = "public, max-age=31536000, immutable";

    /// <summary>Playlists and the thumbnail.</summary>
    public const string ShortCacheControl = "public, max-age=300";

    /// <summary>
    /// Puts every file of an encode under <paramref name="prefix"/>, with its content type and <c>Cache-Control</c> (plan, "Storage
    /// layout"). Segments go first and playlists after them, and <c>master.m3u8</c> goes last, so the master exists only once
    /// everything it names does.
    /// </summary>
    public static async Task UploadOutputAsync(IObjectStore store, string outputDirectory, string prefix, CancellationToken ct)
    {
        var files = Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories)
            .Select(path => (Path: path, Key: $"{prefix}/{Path.GetRelativePath(outputDirectory, path).Replace(Path.DirectorySeparatorChar, '/')}"))
            .OrderBy(file => file.Key == $"{prefix}/{ObjectKeys.Master}" ? 2 : Path.GetExtension(file.Path) == ".m3u8" ? 1 : 0)
            .ThenBy(file => file.Key, StringComparer.Ordinal);
        foreach (var (path, key) in files)
        {
            var (contentType, cacheControl) = Path.GetExtension(path) switch
            {
                ".ts" => ("video/mp2t", SegmentCacheControl),
                ".m3u8" => ("application/vnd.apple.mpegurl", ShortCacheControl),
                ".jpg" => ("image/jpeg", ShortCacheControl),
                var other => throw new InvalidOperationException($"The encode wrote a file of an unexpected type: '{other}'."),
            };
            await store.UploadAsync(key, path, contentType, cacheControl, ct);
        }
    }

    private async Task RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var result = await runner.RunAsync(options.Value.FfmpegPath, arguments, ct);
        if (result.ExitCode != 0)
        {
            throw new PermanentProcessingException("encode-failed", Ffprobe.Detail(result));
        }
    }
}
