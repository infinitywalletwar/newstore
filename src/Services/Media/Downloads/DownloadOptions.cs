namespace NewStore.Media;

/// <summary>Bound from <c>Media:Downloads</c>.</summary>
public sealed class DownloadOptions
{
    public const string SectionName = "Media:Downloads";

    /// <summary>How long a download link works: 15 minutes. A download that starts in time finishes after the link expires.</summary>
    public TimeSpan LinkLifetime { get; set; } = TimeSpan.FromMinutes(15);
}

public static class DownloadExtensions
{
    /// <summary>Binds <c>Media:Downloads</c> and checks it when the host starts.</summary>
    public static IHostApplicationBuilder AddDownloads(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOptions<DownloadOptions>()
            .BindConfiguration(DownloadOptions.SectionName)
            .Validate(o => o.LinkLifetime > TimeSpan.Zero, "Media:Downloads:LinkLifetime must be positive.")
            .ValidateOnStart();
        return builder;
    }
}
