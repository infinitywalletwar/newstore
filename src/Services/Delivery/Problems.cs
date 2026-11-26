namespace NewStore.Delivery;

/// <summary>Error replies as <c>application/problem+json</c>, with a stable <c>type</c> of <c>urn:newstore:problem:&lt;slug&gt;</c>.</summary>
internal static class Problems
{
    private static string Type(string slug) => $"urn:newstore:problem:{slug}";

    public static IResult NotFound(string what) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: $"{what} not found.", type: Type("not-found"));
}
