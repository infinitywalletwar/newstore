using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Options;

namespace NewStore.Media.Processing;

/// <summary>Reads what an uploaded file is, and refuses what the worker will not encode (plan, "Accepted files").</summary>
public sealed class Ffprobe(FfmpegRunner runner, IOptions<ProcessingOptions> options)
{
    /// <summary>The two demuxers ffprobe may name: MP4 and MOV share the first, WebM is the second.</summary>
    public static readonly IReadOnlySet<string> AcceptedFormats = new HashSet<string>(StringComparer.Ordinal)
    {
        "mov,mp4,m4a,3gp,3g2,mj2",
        "matroska,webm",
    };

    /// <summary>
    /// ffprobe may read local files only (<c>-protocol_whitelist file</c>), and only as MP4/MOV or Matroska/WebM
    /// (<c>-format_whitelist</c>), so a playlist or a concat script that names other files is refused before anything opens them.
    /// </summary>
    public static IReadOnlyList<string> ProbeArguments(string source) =>
    [
        "-v", "error", "-protocol_whitelist", "file", "-format_whitelist", "mov,matroska",
        "-print_format", "json", "-show_format", "-show_streams", source,
    ];

    /// <exception cref="PermanentProcessingException">
    /// <c>unreadable</c>, <c>unsupported-format</c>, <c>no-video-stream</c> or <c>zero-duration</c>.
    /// </exception>
    public async Task<MediaInfo> ProbeAsync(string source, CancellationToken ct)
    {
        var result = await runner.RunAsync(options.Value.FfprobePath, ProbeArguments(source), ct);
        if (result.ExitCode != 0)
        {
            throw new PermanentProcessingException("unreadable", Detail(result));
        }

        Probe probe;
        try
        {
            probe = JsonSerializer.Deserialize<Probe>(result.StandardOutput, JsonOptions)
                ?? throw new JsonException("ffprobe printed null.");
        }
        catch (JsonException ex)
        {
            throw new PermanentProcessingException("unreadable", $"ffprobe's output is not the expected JSON: {ex.Message}");
        }

        var format = probe.Format?.FormatName ?? "";
        if (!AcceptedFormats.Contains(format))
        {
            throw new PermanentProcessingException("unsupported-format", $"ffprobe read the file as '{format}'.");
        }

        var streams = probe.Streams ?? [];
        var video = streams.FirstOrDefault(s => s.CodecType == "video")
            ?? throw new PermanentProcessingException("no-video-stream", "The file has no video stream.");

        var duration = double.TryParse(probe.Format?.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
        if (duration <= 0)
        {
            throw new PermanentProcessingException("zero-duration", $"ffprobe reports a duration of '{probe.Format?.Duration}'.");
        }

        return new MediaInfo(
            format,
            duration,
            HasAudio: streams.Any(s => s.CodecType == "audio"),
            video.Width ?? 0,
            video.Height ?? 0,
            FramesPerSecond(video.AvgFrameRate));
    }

    /// <summary>ffprobe's <c>avg_frame_rate</c> is a fraction such as <c>30000/1001</c>; <c>0/0</c> (unknown) counts as 30.</summary>
    public static double FramesPerSecond(string? averageFrameRate)
    {
        var parts = (averageFrameRate ?? "").Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && numerator > 0 && denominator > 0)
        {
            return numerator / denominator;
        }

        return 30;
    }

    internal static string Detail(FfmpegResult result) =>
        $"exit code {result.ExitCode}: {string.Join('\n', result.LastErrorLines)}";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed record Probe(List<ProbeStream>? Streams, ProbeFormat? Format);

    private sealed record ProbeStream(string? CodecType, int? Width, int? Height, string? AvgFrameRate);

    private sealed record ProbeFormat(string? FormatName, string? Duration);
}
