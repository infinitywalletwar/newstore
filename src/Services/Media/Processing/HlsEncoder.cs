using System.Globalization;

using Microsoft.Extensions.Options;

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
        var at = Math.Min(5, info.DurationSeconds / 2).ToString("0.###", CultureInfo.InvariantCulture);
        return RunAsync(
        [
            "-nostdin", "-y", "-protocol_whitelist", "file", "-format_whitelist", "mov,matroska",
            "-ss", at, "-i", source, "-frames:v", "1", "-vf", "scale=-2:360", Path.Combine(outputDirectory, "thumbnail.jpg"),
        ], ct);
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

    private async Task RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var result = await runner.RunAsync(options.Value.FfmpegPath, arguments, ct);
        if (result.ExitCode != 0)
        {
            throw new PermanentProcessingException("encode-failed", Ffprobe.Detail(result));
        }
    }
}
