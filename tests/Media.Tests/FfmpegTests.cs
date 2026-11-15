using System.Diagnostics;
using System.Globalization;

using Microsoft.Extensions.Options;

using NewStore.Media.Processing;

namespace NewStore.Media.Tests;

/// <summary>ffprobe and ffmpeg as the worker runs them, on the samples, with no host around them.</summary>
public sealed class FfmpegTests(SampleVideos samples) : IDisposable
{
    private const string Mp4Demuxer = "mov,mp4,m4a,3gp,3g2,mj2";

    private static readonly IOptions<ProcessingOptions> Defaults = Options.Create(new ProcessingOptions());

    private readonly FfmpegRunner _runner = new();
    private readonly string _work = Path.Combine(Path.GetTempPath(), $"newstore-media-ffmpeg-{Guid.NewGuid():N}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Ffprobe Probe => new(_runner, Defaults);

    private HlsEncoder Encoder => new(_runner, Defaults);

    public void Dispose()
    {
        if (Directory.Exists(_work))
        {
            Directory.Delete(_work, recursive: true);
        }
    }

    [Fact]
    public async Task Probe_reads_format_duration_audio_and_frame_rate()
    {
        var info = await Probe.ProbeAsync(await samples.Mp4Async(), Ct);
        var silent = await Probe.ProbeAsync(await samples.NoAudioAsync(), Ct);
        var webm = await Probe.ProbeAsync(await samples.WebmAsync(), Ct);

        Assert.Equal(Mp4Demuxer, info.FormatName);
        Assert.InRange(info.DurationSeconds, 7.9, 8.1);
        Assert.True(info.HasAudio);
        Assert.Equal((1280, 720), (info.Width, info.Height));
        Assert.Equal(25, info.FramesPerSecond, precision: 3);
        Assert.False(silent.HasAudio);
        Assert.Equal("matroska,webm", webm.FormatName);
    }

    [Fact]
    public async Task Probe_of_random_bytes_is_unreadable()
    {
        var source = await samples.RandomBytesAsync();

        var refused = await Assert.ThrowsAsync<PermanentProcessingException>(() => Probe.ProbeAsync(source, Ct));

        Assert.Equal("unreadable", refused.Reason);
    }

    [Fact]
    public async Task Probe_of_a_disguised_playlist_is_refused()
    {
        var source = await samples.DisguisedPlaylistAsync();

        var refused = await Assert.ThrowsAsync<PermanentProcessingException>(() => Probe.ProbeAsync(source, Ct));

        Assert.True(refused.Reason is "unreadable" or "unsupported-format", $"The reason is {refused.Reason}.");
    }

    [Fact]
    public async Task Probe_of_a_concat_script_is_refused_by_the_whitelist()
    {
        var source = await samples.ConcatScriptAsync();

        var refused = await Assert.ThrowsAsync<PermanentProcessingException>(() => Probe.ProbeAsync(source, Ct));

        Assert.True(refused.Reason is "unreadable" or "unsupported-format", $"The reason is {refused.Reason}.");
        // The whitelist refused it. Without -format_whitelist the concat demuxer runs and only its own "safe" check stops
        // /etc/hosts ("Unsafe file name"), which a relative name in the script would pass.
        Assert.Contains("Format not on whitelist", refused.Message);
    }

    [Fact]
    public void Every_command_reads_its_input_through_the_whitelists()
    {
        var info = new MediaInfo(Mp4Demuxer, 8, HasAudio: true, 1280, 720, FramesPerSecond: 25);
        var source = Path.Combine("work", "source");
        var output = Path.Combine("work", "out");

        AssertWhitelisted(Ffprobe.ProbeArguments(source), source);
        AssertWhitelisted(HlsEncoder.ThumbnailArguments(source, info, output), source);
        AssertWhitelisted(HlsEncoder.EncodeArguments(source, info, output, "medium"), source);

        static void AssertWhitelisted(IReadOnlyList<string> arguments, string source)
        {
            var list = arguments.ToList();
            var at = list.IndexOf("-protocol_whitelist");
            Assert.True(at >= 0, $"No -protocol_whitelist in: {string.Join(' ', list)}");
            Assert.Equal(["-protocol_whitelist", "file", "-format_whitelist", "mov,matroska"], list.Skip(at).Take(4));
            // Input options apply to the input after them, so they come before the source.
            Assert.True(at < list.IndexOf(source), $"The whitelists come after the source in: {string.Join(' ', list)}");
        }
    }

    [Fact]
    public void Encode_arguments_match_the_plan()
    {
        var withAudio = new MediaInfo(Mp4Demuxer, 8, HasAudio: true, 1280, 720, FramesPerSecond: 30);
        var silent = withAudio with { HasAudio = false, FramesPerSecond = 25 };
        var source = Path.Combine("work", "source");
        var output = Path.Combine("work", "out");

        var arguments = HlsEncoder.EncodeArguments(source, withAudio, output, "medium");
        var silentArguments = HlsEncoder.EncodeArguments(source, silent, output, "veryfast");

        // The plan's command (Global Constraints, "The FFmpeg commands"), one argument per entry.
        Assert.Equal(
        [
            "-nostdin", "-y", "-protocol_whitelist", "file", "-format_whitelist", "mov,matroska", "-i", source,
            "-map", "0:v:0", "-map", "0:a:0", "-map", "0:v:0", "-map", "0:a:0",
            "-filter:v:0", "scale=-2:360", "-b:v:0", "800k", "-filter:v:1", "scale=-2:720", "-b:v:1", "2800k",
            "-c:v", "libx264", "-preset", "medium", "-pix_fmt", "yuv420p",
            "-g", "180", "-keyint_min", "180", "-sc_threshold", "0", "-c:a", "aac", "-b:a", "128k", "-ac", "2",
            "-f", "hls", "-hls_time", "6", "-hls_playlist_type", "vod", "-hls_flags", "independent_segments",
            "-master_pl_name", "master.m3u8", "-var_stream_map", "v:0,a:0 v:1,a:1",
            "-hls_segment_filename", Path.Combine(output, "v%v", "seg_%03d.ts"), Path.Combine(output, "v%v", "index.m3u8"),
        ], arguments);

        // No audio stream: no audio maps, no audio codec, video-only variants. 25 fps: a key frame every 6 s is 150 frames.
        Assert.Equal("v:0 v:1", After(silentArguments, "-var_stream_map"));
        Assert.DoesNotContain("-c:a", silentArguments);
        Assert.DoesNotContain("0:a:0", silentArguments);
        Assert.Equal("150", After(silentArguments, "-g"));
        Assert.Equal("150", After(silentArguments, "-keyint_min"));
        Assert.Equal("veryfast", After(silentArguments, "-preset"));
    }

    [Fact]
    public async Task Encode_makes_two_variants_and_a_thumbnail()
    {
        var source = await samples.Mp4Async();
        var info = await Probe.ProbeAsync(source, Ct);

        await Encoder.EncodeAsync(source, info, _work, Ct);
        await Encoder.ThumbnailAsync(source, info, _work, Ct);

        var master = await File.ReadAllTextAsync(Path.Combine(_work, "master.m3u8"), Ct);
        Assert.Contains("RESOLUTION=640x360", master);
        Assert.Contains("RESOLUTION=1280x720", master);
        foreach (var variant in new[] { "v0", "v1" })
        {
            var playlist = await File.ReadAllLinesAsync(Path.Combine(_work, variant, "index.m3u8"), Ct);
            var durations = playlist
                .Where(line => line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                .Select(line => double.Parse(line["#EXTINF:".Length..].TrimEnd(','), CultureInfo.InvariantCulture))
                .ToList();
            Assert.NotEmpty(durations);
            Assert.All(durations, seconds => Assert.True(seconds <= 6.0, $"{variant} has a segment of {seconds} s"));
            Assert.Contains("#EXT-X-ENDLIST", playlist);
        }

        var thumbnail = await File.ReadAllBytesAsync(Path.Combine(_work, "thumbnail.jpg"), Ct);
        Assert.Equal([0xFF, 0xD8], thumbnail[..2]);   // a JPEG starts with the SOI marker
    }

    [Fact]
    public async Task Runner_kills_ffmpeg_when_cancelled()
    {
        var source = await samples.LongAsync();
        var info = await Probe.ProbeAsync(source, Ct);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var encode = Encoder.EncodeAsync(source, info, _work, cancel.Token);
        var firstSegment = Path.Combine(_work, "v0", "seg_000.ts");
        await WaitUntilAsync(() => File.Exists(firstSegment), TimeSpan.FromSeconds(30));
        var stopwatch = Stopwatch.StartNew();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => encode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"The cancelled encode took {stopwatch.Elapsed} to stop.");

        // Killed, not left running: nothing more is written, and the encode never finished its playlists.
        var written = BytesUnder(_work);
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        Assert.Equal(written, BytesUnder(_work));
        Assert.DoesNotContain("#EXT-X-ENDLIST", File.Exists(Path.Combine(_work, "v0", "index.m3u8"))
            ? await File.ReadAllTextAsync(Path.Combine(_work, "v0", "index.m3u8"), Ct)
            : "");
    }

    private static string After(IReadOnlyList<string> arguments, string option) => arguments[arguments.ToList().IndexOf(option) + 1];

    private static long BytesUnder(string directory) =>
        new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Not true after {timeout}.");
            await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
        }
    }
}
