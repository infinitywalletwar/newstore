using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace NewStore.Media.Tests;

/// <summary>
/// The test videos, made with the local <c>ffmpeg</c> from <c>lavfi</c>'s generated picture and tone, once per test run: each is
/// made the first time a test asks for it. An assembly fixture, so one folder serves the run and is deleted at its end.
/// Unless a name says otherwise, a sample is 8 s of 1280x720 at 25 fps with a stereo tone.
/// </summary>
public sealed class SampleVideos : IAsyncDisposable
{
    public const string MissingFfmpeg = "Media's tests need FFmpeg: brew install ffmpeg, or apt-get install ffmpeg";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"newstore-media-samples-{Guid.NewGuid():N}");
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _samples = new();

    /// <summary>H.264 and AAC in MP4.</summary>
    public Task<string> Mp4Async() => Video("landscape.mp4", seconds: 8, "1280x720", audio: true);

    /// <summary>60 s, long enough that a test can stop the encode halfway.</summary>
    public Task<string> LongAsync() => Video("long.mp4", seconds: 60, "1280x720", audio: true);

    public Task<string> NoAudioAsync() => Video("silent.mp4", seconds: 8, "1280x720", audio: false);

    /// <summary>720x1280, as a phone films upright.</summary>
    public Task<string> PortraitAsync() => Video("portrait.mp4", seconds: 8, "720x1280", audio: true);

    /// <summary>H.264 and AAC in QuickTime.</summary>
    public Task<string> MovAsync() => Video("landscape.mov", seconds: 8, "1280x720", audio: true);

    /// <summary>VP9 and Opus in WebM.</summary>
    public Task<string> WebmAsync() => Video("landscape.webm", seconds: 8, "1280x720", audio: true);

    /// <summary>200 KB of random bytes named as a video: no demuxer reads it.</summary>
    public Task<string> RandomBytesAsync() => Once("random.mp4", async path =>
    {
        var bytes = new byte[200 * 1024];
        new Random(20261115).NextBytes(bytes);
        await File.WriteAllBytesAsync(path, bytes);
    });

    /// <summary>
    /// An HLS playlist named as a video, whose one segment is <c>file:///etc/hosts</c>. Read as HLS, it would make FFmpeg open a
    /// local file the uploader chose. FFmpeg's HLS demuxer itself refuses a playlist whose name doesn't end in <c>.m3u8</c> (9.0.2
    /// and 6.1.1 both print "Not detecting m3u8/hls with non standard extension"), so this sample never reaches the whitelist;
    /// <see cref="ConcatScriptAsync"/> does.
    /// </summary>
    public Task<string> DisguisedPlaylistAsync() => Once("playlist.mp4", path => File.WriteAllTextAsync(path, """
        #EXTM3U
        #EXT-X-VERSION:3
        #EXT-X-TARGETDURATION:10
        #EXTINF:10.0,
        file:///etc/hosts
        #EXT-X-ENDLIST

        """));

    /// <summary>
    /// An <c>ffconcat</c> script naming <c>/etc/hosts</c>, saved as <c>source</c>: the name the worker gives every download, with
    /// no extension. FFmpeg recognises the concat demuxer by the content, so only <c>-format_whitelist</c> stops it before the
    /// demuxer runs.
    /// </summary>
    public Task<string> ConcatScriptAsync() => Once(Path.Combine("concat", "source"), path => File.WriteAllTextAsync(path, """
        ffconcat version 1.0
        file /etc/hosts

        """));

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private Task<string> Video(string name, int seconds, string size, bool audio) => Once(name, path =>
    {
        List<string> arguments = ["-nostdin", "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", $"testsrc2=size={size}:rate=25:duration={seconds}"];
        if (audio)
        {
            arguments.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={seconds}", "-ac", "2"]);
        }

        // Fast and small: these files only need to be valid input, the code under test does the real encode.
        arguments.AddRange(name.EndsWith(".webm", StringComparison.Ordinal)
            ? ["-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-b:v", "1M", "-c:a", "libopus", "-b:a", "96k"]
            : ["-c:v", "libx264", "-preset", "ultrafast", "-b:v", "1M", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k"]);
        arguments.Add(path);
        return RunFfmpegAsync(arguments);
    });

    private Task<string> Once(string name, Func<string, Task> make) =>
        _samples.GetOrAdd(name, _ => new Lazy<Task<string>>(async () =>
        {
            var path = Path.Combine(_directory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await make(path);
            return path;
        })).Value;

    internal static async Task RunFfmpegAsync(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(MissingFfmpeg, ex);
        }

        using (process)
        {
            var errors = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"ffmpeg could not make a sample ({process.ExitCode}): {errors}");
            }
        }
    }
}
