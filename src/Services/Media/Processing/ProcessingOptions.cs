using NewStore.Media.Storage;

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

public static class ProcessingExtensions
{
    /// <summary>x264's presets, fastest first (FFmpeg lists them when given another).</summary>
    private static readonly string[] X264Presets =
        ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow", "placebo"];

    /// <summary>
    /// Binds and checks <c>Media:Processing</c> and registers the queue, FFmpeg and the encoder in every role, so the seed can encode
    /// its sample. The <see cref="ProcessingWorker"/> runs only when <paramref name="runWorker"/> is true.
    /// </summary>
    public static IHostApplicationBuilder AddProcessing(this IHostApplicationBuilder builder, bool runWorker)
    {
        builder.Services.AddOptions<ProcessingOptions>()
            .BindConfiguration(ProcessingOptions.SectionName)
            .Validate(o => o.PollInterval > TimeSpan.Zero, "Media:Processing:PollInterval must be positive.")
            .Validate(o => o.Lease > TimeSpan.Zero, "Media:Processing:Lease must be positive.")
            .Validate(o => o.MaxAttempts >= 1, "Media:Processing:MaxAttempts must be at least 1.")
            .Validate(o => o.RetryPause > TimeSpan.Zero && o.MaxRetryPause >= o.RetryPause,
                "Media:Processing:RetryPause must be positive and MaxRetryPause at least as long.")
            .Validate(o => o.JobTimeout > TimeSpan.Zero, "Media:Processing:JobTimeout must be positive.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.WorkDirectory), "Media:Processing:WorkDirectory is missing.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.FfmpegPath) && !string.IsNullOrWhiteSpace(o.FfprobePath),
                "Media:Processing:FfmpegPath and FfprobePath are required.")
            .Validate(o => X264Presets.Contains(o.X264Preset), $"Media:Processing:X264Preset must be one of {string.Join(", ", X264Presets)}.")
            .ValidateOnStart();

        builder.Services.AddSingleton<FfmpegRunner>();
        builder.Services.AddSingleton<Ffprobe>();
        builder.Services.AddSingleton<HlsEncoder>();
        builder.Services.AddSingleton<IObjectStore, S3ObjectStore>();
        builder.Services.AddScoped<JobQueue>();
        builder.Services.AddScoped<JobRunner>();
        if (runWorker)
        {
            builder.Services.AddHostedService<ProcessingWorker>();
        }

        return builder;
    }
}
