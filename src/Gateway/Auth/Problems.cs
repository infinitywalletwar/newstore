namespace NewStore.Gateway.Auth;

/// <summary>Error replies as <c>application/problem+json</c> with a stable <c>type</c> of <c>urn:newstore:problem:&lt;slug&gt;</c>, as the services do.</summary>
internal static class Problems
{
    private static string Type(string slug) => $"urn:newstore:problem:{slug}";

    public static IResult SignInRequired() =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sign in to use this.", type: Type("unauthorized"));

    public static IResult SessionExpired() =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Your session ended. Sign in again.", type: Type("session-expired"));

    public static IResult Forbidden() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "You may not use this.", type: Type("forbidden"));

    public static IResult Csrf() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Requests that use the session cookie must send the X-CSRF: 1 header.", type: Type("csrf"));

    public static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such resource.", type: Type("not-found"));

    public static IResult BadReturnUrl() =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "returnUrl must be a path on this site.", type: Type("bad-return-url"));

    public static IResult BadSessionId() =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "sid is missing or is not this session's id.", type: Type("bad-session-id"));
}
