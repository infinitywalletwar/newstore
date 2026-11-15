namespace NewStore.Media;

/// <summary>The one rule for a file name that goes into a <c>Content-Disposition</c> header and into the API's reply.</summary>
public static class SafeFileName
{
    /// <summary>
    /// Every character outside <c>[A-Za-z0-9._-]</c> becomes <c>_</c>. The header value is built from text a creator typed, so the name
    /// cannot end the quoted string, add a header parameter or carry a control character. <c>my "trip".mp4</c> becomes <c>my__trip_.mp4</c>.
    /// </summary>
    public static string Of(string fileName) =>
        string.Concat(fileName.Select(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-' ? c : '_'));
}
