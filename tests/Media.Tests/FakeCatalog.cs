using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;

namespace NewStore.Media.Tests;

/// <summary>
/// Catalog as Media's tests see it: a table of videos behind <see cref="StubHttpHandler"/>. A test registers the videos it uses;
/// any other video id is unknown (404). Video ids are random, so tests share the table without clashing.
/// </summary>
internal static class FakeCatalog
{
    private static readonly ConcurrentDictionary<Guid, (Guid CreatorId, string Status)> Videos = new();
    private static readonly ConcurrentDictionary<Guid, bool> Unreachable = new();

    public static void SetVideo(Guid videoId, Guid creatorId, string status = "Draft") => Videos[videoId] = (creatorId, status);

    /// <summary>Catalog answers 503 for this video, as when it is down.</summary>
    public static void Break(Guid videoId) => Unreachable[videoId] = true;

    public static StubHttpHandler Create() => new StubHttpHandler().On(HttpMethod.Get, "/videos/{id}/owner", request =>
    {
        var videoId = Guid.Parse(request.RequestUri!.AbsolutePath.Split('/')[2]);
        if (Unreachable.ContainsKey(videoId))
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        return Videos.TryGetValue(videoId, out var video)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { videoId, creatorId = video.CreatorId, status = video.Status }) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    });
}
