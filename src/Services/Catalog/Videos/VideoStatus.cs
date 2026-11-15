namespace NewStore.Catalog.Videos;

/// <summary>What viewers can see. Stored as the name, so the column does not depend on the enum's numbering.</summary>
public enum VideoStatus
{
    /// <summary>Created by its creator; Media has not published it. Only <c>/me/videos</c> shows it.</summary>
    Draft,

    /// <summary>Media published it: it is listed, searched and playable.</summary>
    Published,
}
