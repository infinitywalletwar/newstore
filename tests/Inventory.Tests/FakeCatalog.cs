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
    private static readonly ConcurrentDictionary<Guid, Guid> Owners = new();
    private static readonly ConcurrentDictionary<Guid, bool> Unreachable = new();

    public static void SetOwner(Guid videoId, Guid creatorId) => Owners[videoId] = creatorId;

    /// <summary>Catalog answers 503 for this video, as when it is down.</summary>
    public static void Break(Guid videoId) => Unreachable[videoId] = true;

    public static StubHttpHandler Create() => new StubHttpHandler().On(HttpMethod.Get, "/videos/{id}/owner", request =>
    {
        var videoId = Guid.Parse(request.RequestUri!.AbsolutePath.Split('/')[2]);
        if (Unreachable.ContainsKey(videoId))
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        return Owners.TryGetValue(videoId, out var creatorId)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { videoId, creatorId }) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    });
}
