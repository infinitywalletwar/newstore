using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;

namespace NewStore.Inventory.Tests;

/// <summary>
/// Catalog as Inventory's tests see it: a table of video owners behind <see cref="StubHttpHandler"/>. A test registers the
/// videos it uses; any other video id is unknown (404). Video ids are random, so tests share the table without clashing.
/// </summary>
internal static class FakeCatalog
{
    private static readonly ConcurrentDictionary<Guid, (Guid CreatorId, string Status)> Owners = new();
    private static readonly ConcurrentDictionary<Guid, bool> Unreachable = new();

    /// <summary>Registers a video. Like Catalog's reply, it is <c>Published</c> unless the test says <c>Draft</c>.</summary>
    public static void SetOwner(Guid videoId, Guid creatorId, string status = "Published") => Owners[videoId] = (creatorId, status);

    /// <summary>Catalog answers 503 for this video, as when it is down.</summary>
    public static void Break(Guid videoId) => Unreachable[videoId] = true;

    public static StubHttpHandler Create() => new StubHttpHandler().On(HttpMethod.Get, "/videos/{id}/owner", request =>
    {
        var videoId = Guid.Parse(request.RequestUri!.AbsolutePath.Split('/')[2]);
        if (Unreachable.ContainsKey(videoId))
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        return Owners.TryGetValue(videoId, out var video)
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { videoId, creatorId = video.CreatorId, status = video.Status }),
            }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    });
}
