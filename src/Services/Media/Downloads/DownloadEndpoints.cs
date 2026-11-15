using System.Security.Claims;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using NewStore.ServiceDefaults.Security;

namespace NewStore.Media;

public sealed record DownloadRequest(Guid VideoId);

public sealed record DownloadLink(string Url, DateTimeOffset ExpiresAt, string FileName);

public static class DownloadEndpoints
{
    public static IEndpointRouteBuilder MapDownloadEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/downloads", CreateAsync).RequireAuthorization(Policies.SignedIn);
        return routes;
    }

    /// <summary>
    /// A link is a bearer: whoever holds it can use it until it expires, so "only the owner" means only the owner gets one.
    /// The answer carries <c>no-store</c> so no cache keeps a link for someone else.
    /// </summary>
    private static async Task<IResult> CreateAsync(
        DownloadRequest request, ClaimsPrincipal user, MediaDbContext db, MediaStorage storage, TimeProvider time,
        IOptions<DownloadOptions> options, HttpContext http, CancellationToken ct)
    {
        var owner = user.UserId();
        if (!await db.DigitalCopies.AnyAsync(c => c.OwnerId == owner && c.VideoId == request.VideoId, ct))
        {
            return Problems.Forbidden("no-digital-copy", "You do not own a digital copy of this video.");
        }

        // A video has at most one live upload (ix_uploads_live_video), so at most one is Published.
        // The key is the one the row stores: a seeded sample's original is not at originals/{videoId}/{uploadId}.
        var upload = await db.Uploads.AsNoTracking()
            .Where(u => u.VideoId == request.VideoId && u.Status == UploadStatus.Published)
            .Select(u => new { u.ObjectKey, u.FileName })
            .SingleOrDefaultAsync(ct);
        if (upload is null)
        {
            return Problems.NotFound("no-original", "This video has no original to download.");
        }

        var lifetime = options.Value.LinkLifetime;
        var fileName = SafeFileName.Of(upload.FileName);
        var url = await storage.PresignDownloadAsync(upload.ObjectKey, upload.FileName, lifetime);

        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new DownloadLink(url, time.GetUtcNowToMicrosecond().Add(lifetime), fileName));
    }
}
