using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace NewStore.Media.Tests;

public sealed record ProblemDto(string? Type, int Status, string? Title, Dictionary<string, string[]>? Errors, Guid? UploadId);

/// <summary>The service's endpoints, and the browser's part of the upload, as the tests use them.</summary>
internal static class Api
{
    public const long MiB = 1024 * 1024;

    // The browser: a plain HttpClient with no AWS credentials. Redirects and cookies off, so a test sees S3's own answer.
    private static readonly HttpClient Browser = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });

    /// <summary>The time cut to whole microseconds, as the service stores it.</summary>
    public static DateTimeOffset Micro(DateTimeOffset t) => new(t.UtcTicks - (t.UtcTicks % 10), TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string UserToken(Guid userId) => TestTokens.User(userId, "media");

    public static Task<HttpResponseMessage> SendAsUserAsync(
        this HttpClient client, Guid userId, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", UserToken(userId));
        return client.SendAsync(request, Ct);
    }

    /// <summary>Registers a Draft video of <paramref name="creatorId"/> in the fake Catalog and returns its id.</summary>
    public static Guid NewVideo(Guid creatorId, string status = "Draft")
    {
        var videoId = Guid.NewGuid();
        FakeCatalog.SetVideo(videoId, creatorId, status);
        return videoId;
    }

    public static Task<HttpResponseMessage> PostCreateAsync(
        this HttpClient client, Guid creatorId, Guid videoId, long size = 12 * MiB, string fileName = "trip.mp4", string contentType = "video/mp4") =>
        client.SendAsUserAsync(creatorId, HttpMethod.Post, "/uploads", new { videoId, fileName, contentType, sizeBytes = size });

    /// <summary>Creates an upload on a new Draft video of <paramref name="creatorId"/> and returns the 201 body.</summary>
    public static async Task<UploadResponse> CreateAsync(this HttpClient client, Guid creatorId, long size = 12 * MiB, Guid? videoId = null)
    {
        var response = await client.PostCreateAsync(creatorId, videoId ?? NewVideo(creatorId), size);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UploadResponse>(Ct))!;
    }

    public static async Task<UploadResponse> GetAsync(this HttpClient client, Guid creatorId, Guid uploadId)
    {
        var response = await client.SendAsUserAsync(creatorId, HttpMethod.Get, $"/uploads/{uploadId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UploadResponse>(Ct))!;
    }

    public static Task<HttpResponseMessage> PostCompleteAsync(
        this HttpClient client, Guid creatorId, Guid uploadId, IEnumerable<(int PartNumber, string ETag)> parts) =>
        client.SendAsUserAsync(creatorId, HttpMethod.Post, $"/uploads/{uploadId}/complete",
            new { parts = parts.Select(p => new { partNumber = p.PartNumber, eTag = p.ETag }) });

    public static async Task<ProblemDto> ProblemAsync(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemDto>(Ct))!;

    /// <summary>The browser sends <paramref name="size"/> bytes to a part URL. Returns the response.</summary>
    public static Task<HttpResponseMessage> PutPartAsync(string url, long size, byte seed = 1)
    {
        var body = new byte[size];
        Array.Fill(body, seed);
        return Browser.PutAsync(url, new ByteArrayContent(body), Ct);
    }

    /// <summary>The browser sends the part and reads its ETag, as it would after the CORS rule exposes it.</summary>
    public static async Task<(int PartNumber, string ETag)> PutPartOkAsync(PartUrl part)
    {
        using var response = await PutPartAsync(part.Url, part.Size, (byte)part.PartNumber);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (part.PartNumber, response.Headers.ETag!.Tag);
    }
}
