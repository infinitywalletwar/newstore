using Microsoft.Extensions.Options;

namespace NewStore.Media;

/// <summary>Which parts of Media this process runs. <c>All</c> runs both in one process (<c>dotnet run</c>, tests).</summary>
public enum MediaRole { All, Api, Worker }

/// <summary>Bound from <c>Media:Uploads</c>. The defaults are the plan's Global Constraints.</summary>
public sealed class UploadOptions
{
    public const string SectionName = "Media:Uploads";

    /// <summary>The size of every part but the last: 64 MiB. S3 wants at least 5 MiB for each part except the last.</summary>
    public long PartSize { get; set; } = 64L * 1024 * 1024;

    /// <summary>The largest file: 2 GiB.</summary>
    public long MaxSizeBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>How long a presigned part URL works.</summary>
    public TimeSpan PartUrlLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>An upload still <c>Uploading</c> this long after it was created is abandoned.</summary>
    public TimeSpan AbandonAfter { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan ExpirySweepInterval { get; set; } = TimeSpan.FromMinutes(5);
}

public static class MediaOptionsExtensions
{
    /// <summary>
    /// Binds <c>Media:Storage</c> and <c>Media:Uploads</c>, checks them when the host starts, and returns <c>Media:Role</c>
    /// (default <see cref="MediaRole.All"/>).
    /// </summary>
    public static MediaRole AddMediaOptions(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOptions<StorageOptions>()
            .BindConfiguration(StorageOptions.SectionName)
            .Validate(o => IsHttpUrl(o.ServiceUrl), "Media:Storage:ServiceUrl must be an absolute http or https URL.")
            .Validate(o => IsHttpUrl(o.PublicServiceUrl), "Media:Storage:PublicServiceUrl must be an absolute http or https URL: the browser sends the parts there.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Bucket), "Media:Storage:Bucket is missing.")
            .Validate(o => (o.AccessKey is null) == (o.SecretKey is null), "Media:Storage:AccessKey and SecretKey are set together or not at all.")
            .ValidateOnStart();

        builder.Services.AddOptions<UploadOptions>()
            .BindConfiguration(UploadOptions.SectionName)
            // 5 MiB is S3's smallest part (except the last), and 10,000 is its most parts.
            .Validate(o => o.PartSize >= 5L * 1024 * 1024, "Media:Uploads:PartSize must be at least 5 MiB.")
            .Validate(o => o.MaxSizeBytes > 0 && o.MaxSizeBytes <= o.PartSize * 10_000, "Media:Uploads:MaxSizeBytes must fit in 10,000 parts.")
            .Validate(o => o.PartUrlLifetime > TimeSpan.Zero, "Media:Uploads:PartUrlLifetime must be positive.")
            .Validate(o => o.AbandonAfter > TimeSpan.Zero, "Media:Uploads:AbandonAfter must be positive.")
            .Validate(o => o.ExpirySweepInterval > TimeSpan.Zero, "Media:Uploads:ExpirySweepInterval must be positive.")
            .ValidateOnStart();

        return builder.Configuration.GetValue("Media:Role", MediaRole.All);
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}
