namespace NewStore.Orders;

/// <summary>Error replies as <c>application/problem+json</c>, with a stable <c>type</c> of <c>urn:newstore:problem:&lt;slug&gt;</c>.</summary>
internal static class Problems
{
    public static string Type(string slug) => $"urn:newstore:problem:{slug}";

    public static IResult NotFound(string what) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: $"{what} not found.", type: Type("not-found"));

    public static IResult Conflict(string slug, string title) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: title, type: Type(slug));

    public static IResult Validation(Dictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors, type: Type("validation"));

    /// <summary>Inventory's answer for the buyer, as Inventory gave it: its status, its problem type and its title.</summary>
    public static IResult Passed(int statusCode, string type, string title) =>
        Results.Problem(statusCode: statusCode, title: title, type: type);

    /// <summary>Inventory or Payments did not answer. Every call Orders makes is safe to repeat, so the caller may try again.</summary>
    public static IResult DependencyUnavailable(HttpResponse response)
    {
        response.Headers.RetryAfter = "5";
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "A service this order depends on is unavailable. Try again.",
            type: Type("dependency-unavailable"));
    }
}
