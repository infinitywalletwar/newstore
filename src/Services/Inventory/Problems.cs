namespace NewStore.Inventory;

/// <summary>Error replies as <c>application/problem+json</c>, with a stable <c>type</c> of <c>urn:newstore:problem:&lt;slug&gt;</c>.</summary>
internal static class Problems
{
    private static string Type(string slug) => $"urn:newstore:problem:{slug}";

    public static IResult NotFound(string what) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: $"{what} not found.", type: Type("not-found"));

    public static IResult Conflict(string slug, string title) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: title, type: Type(slug));

    public static IResult Validation(Dictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors, type: Type("validation"));
}
