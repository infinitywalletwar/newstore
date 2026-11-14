namespace NewStore.Catalog;

public sealed class CatalogOptions
{
    public const string SectionName = "Catalog";

    /// <summary>
    /// Where a browser reaches the public media files (the bucket's URL). A video's playback and thumbnail URLs are
    /// this value, a slash and the stored path.
    /// </summary>
    public string MediaBaseUrl { get; set; } = "";
}
