namespace NewStore.Payments;

/// <summary>Error replies as <c>application/problem+json</c>, with a stable <c>type</c> of <c>urn:newstore:problem:&lt;slug&gt;</c>.</summary>
internal static class Problems
{
    private static string Type(string slug) => $"urn:newstore:problem:{slug}";

    public static IResult NotFound(string what) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: $"{what} not found.", type: Type("not-found"));

    public static IResult Validation(Dictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors, type: Type("validation"));

    /// <summary>The payment provider did not answer. The caller may try again, and a repeated create is safe.</summary>
    public static IResult DependencyUnavailable(HttpResponse response)
    {
        response.Headers.RetryAfter = "5";
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "The payment provider is unavailable. Try again.",
            type: Type("dependency-unavailable"));
    }

    public static IResult BodyTooLarge() =>
        Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "The webhook body is too large.", type: Type("body-too-large"));

    public static IResult BadWebhook() =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The webhook has a bad signature, a stale timestamp or an unknown type.", type: Type("bad-webhook"));
}
