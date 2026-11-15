namespace NewStore.Media.Processing;

/// <summary>Bound from <c>Media:Processing</c>. The defaults are the plan's Global Constraints.</summary>
public sealed class ProcessingOptions
{
    public const string SectionName = "Media:Processing";

    /// <summary>How often an idle worker asks for a job.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a claim holds a job. The worker renews it every quarter of this, so a live worker never loses it.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Claims of one job, including those of workers that died while holding it.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>The pause after the first transient failure; it doubles after each further one, up to <see cref="MaxRetryPause"/>.</summary>
    public TimeSpan RetryPause { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxRetryPause { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>A job that runs longer fails with <c>timed-out</c>, and FFmpeg is killed.</summary>
    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>The worker's own folder: a job works in <c>{WorkDirectory}/{jobId}</c>, and a starting worker empties it.</summary>
    public string WorkDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "newstore-media");

    public string FfmpegPath { get; set; } = "ffmpeg";

    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>x264's speed against size trade-off. <c>medium</c> is x264's default, and ADR 0018 measured it.</summary>
    public string X264Preset { get; set; } = "medium";
}
