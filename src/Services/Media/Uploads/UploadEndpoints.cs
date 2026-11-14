using System.Security.Claims;

using Microsoft.Extensions.Options;

using NewStore.Messaging;
using NewStore.ServiceDefaults.Security;

namespace NewStore.Media;

public static partial class UploadEndpoints
{
    // Accepted files (plan, "Accepted files"): the extension and the content type must agree.
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".webm"] = "video/webm",
    };

    public static IEndpointRouteBuilder MapUploadEndpoints(this IEndpointRouteBuilder routes)
    {
        // Every upload endpoint is for a signed-in creator, and the creator only ever sees their own uploads.
        var group = routes.MapGroup("").RequireAuthorization(Policies.SignedIn);
        group.MapPost("/uploads", CreateAsync);
        group.MapGet("/uploads/{id:guid}", GetAsync);
        group.MapPost("/uploads/{id:guid}/complete", CompleteAsync);
        group.MapDelete("/uploads/{id:guid}", CancelAsync);
        group.MapGet("/me/uploads", ListMineAsync);
        return routes;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Catalog could not answer for video {VideoId}.")]
    private static partial void LogCatalogUnavailable(ILogger logger, Exception exception, Guid videoId);

    private static async Task<IResult> CreateAsync(
        CreateUploadRequest request, ClaimsPrincipal user, UploadService uploads, CatalogClient catalog,
        IOptions<UploadOptions> options, HttpContext http, ILogger<CatalogClient> logger, CancellationToken ct)
    {
        if (Validate(request, options.Value) is { Count: > 0 } errors)
        {
            return Problems.Validation(errors);
        }

        CatalogVideo? video;
        try
        {
            video = await catalog.GetVideoAsync(request.VideoId, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TransientFailureException)
        {
            LogCatalogUnavailable(logger, e, request.VideoId);
            http.Response.Headers.RetryAfter = "5";
            return Problems.Unavailable("dependency-unavailable", "The catalog is not reachable. Try again shortly.");
        }

        if (video is null)
        {
            return Problems.Validation(new() { ["videoId"] = ["videoId names no video."] });
        }

        var creatorId = user.UserId();
        if (video.CreatorId != creatorId)
        {
            return Problems.Forbidden("not-your-video", "This video belongs to another creator.");
        }

        if (video.Status != "Draft")
        {
            return Problems.Conflict("video-not-draft", "Only a draft video takes a file.");
        }

        switch (await uploads.CreateAsync(creatorId, request, ct))
        {
            case CreateOutcome.Created created:
                return Results.Created($"/uploads/{created.Upload.Id}", await uploads.ToResponseAsync(created.Upload, ct));
            case CreateOutcome.Exists exists:
                return Problems.Conflict("upload-exists", "This video already has an upload.", new Dictionary<string, object?> { ["uploadId"] = exists.UploadId });
            default:
                throw new InvalidOperationException("Unknown outcome.");
        }
    }

    private static Dictionary<string, string[]> Validate(CreateUploadRequest request, UploadOptions options)
    {
        var errors = new Dictionary<string, string[]>();
        var name = request.FileName;
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255)
        {
            errors["fileName"] = ["fileName must have 1 to 255 characters."];
        }
        else if (!ContentTypes.TryGetValue(Path.GetExtension(name), out var expected))
        {
            errors["fileName"] = [$"The extension must be one of {string.Join(", ", ContentTypes.Keys)}."];
        }
        else if (!string.Equals(request.ContentType, expected, StringComparison.OrdinalIgnoreCase))
        {
            errors["contentType"] = [$"A {Path.GetExtension(name).ToLowerInvariant()} file has the content type {expected}."];
        }

        if (request.SizeBytes < 1 || request.SizeBytes > options.MaxSizeBytes)
        {
            errors["sizeBytes"] = [$"sizeBytes must be from 1 to {options.MaxSizeBytes}."];
        }

        return errors;
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal user, UploadService uploads, CancellationToken ct) =>
        await uploads.FindAsync(user.UserId(), id, ct) is { } upload
            ? Results.Ok(await uploads.ToResponseAsync(upload, ct))
            : Problems.NotFound("Upload");

    private static async Task<IResult> CompleteAsync(
        Guid id, CompleteUploadRequest request, ClaimsPrincipal user, UploadService uploads, HttpContext http, CancellationToken ct)
    {
        switch (await uploads.CompleteAsync(user.UserId(), id, request, ct))
        {
            case CompleteOutcome.Done done:
                return Results.Ok(await uploads.ToResponseAsync(done.Upload, ct));
            case CompleteOutcome.Expired:
                return Problems.Conflict("upload-expired", "The upload expired. Start a new one.");
            case CompleteOutcome.BadParts bad:
                return Problems.Validation(new() { ["parts"] = [bad.Message] });
            case CompleteOutcome.InvalidParts invalid:
                return Problems.BadRequest("invalid-parts", $"Storage refused the parts: {invalid.Message}");
            case CompleteOutcome.StorageUnavailable:
                http.Response.Headers.RetryAfter = "5";
                return Problems.Unavailable("storage-unavailable", "Storage is not reachable. Send the same request again shortly.");
            default:
                return Problems.NotFound("Upload");
        }
    }

    private static async Task<IResult> CancelAsync(Guid id, ClaimsPrincipal user, UploadService uploads, CancellationToken ct) =>
        await uploads.CancelAsync(user.UserId(), id, ct) switch
        {
            CancelOutcome.Done => Results.NoContent(),
            CancelOutcome.NotUploading => Problems.Conflict("not-uploading", "Only an upload in progress can be cancelled."),
            _ => Problems.NotFound("Upload"),
        };

    private static async Task<IResult> ListMineAsync(ClaimsPrincipal user, UploadService uploads, CancellationToken ct) =>
        Results.Ok((await uploads.LatestPerVideoAsync(user.UserId(), ct)).Select(UploadSummary.From));
}
