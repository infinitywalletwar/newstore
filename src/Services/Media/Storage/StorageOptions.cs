namespace NewStore.Media;

/// <summary>Bound from <c>Media:Storage</c>.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Media:Storage";

    /// <summary>Where Media's own requests go. Compose: <c>http://floci:4566</c>.</summary>
    public required string ServiceUrl { get; set; }

    /// <summary>The address the browser uses, which the part URLs are signed for. Compose: <c>http://localhost:4566</c>.</summary>
    public required string PublicServiceUrl { get; set; }

    public string Region { get; set; } = "us-east-1";

    /// <summary>Null: the SDK's default credentials (an IAM role in Phase 6).</summary>
    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }
    public string Bucket { get; set; } = "newstore-media";
    public List<string> CorsOrigins { get; set; } = [];
}
