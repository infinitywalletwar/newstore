using System.Net;

namespace NewStore.TestSupport;

/// <summary>
/// Stands in for another service: answers requests from a table of routes and records them. A request that matches no
/// route gets 404, so a test notices a call it did not expect.
/// </summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly List<(HttpMethod Method, string Pattern, Func<HttpRequestMessage, HttpResponseMessage> Reply)> _routes = [];
    private readonly List<HttpRequestMessage> _requests = [];
    private readonly Lock _gate = new();

    /// <summary>Every request received, in order. Each is a copy with its body already read, so a test can read it after the call.</summary>
    public IReadOnlyList<HttpRequestMessage> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>
    /// Answers <paramref name="method"/> requests whose path matches <paramref name="pathPattern"/>. A segment written
    /// <c>{name}</c> matches any one segment, for example <c>/editions/{id}/reservations</c>.
    /// </summary>
    public StubHttpHandler On(HttpMethod method, string pathPattern, Func<HttpRequestMessage, HttpResponseMessage> reply)
    {
        _routes.Add((method, pathPattern, reply));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var copy = await SnapshotAsync(request, cancellationToken);
        lock (_gate)
        {
            _requests.Add(copy);
        }

        var path = request.RequestUri!.AbsolutePath;
        foreach (var (method, pattern, reply) in _routes)
        {
            if (method == request.Method && Matches(pattern, path))
            {
                return reply(copy);
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"No stub for {request.Method} {path}") };
    }

    private static bool Matches(string pattern, string path)
    {
        var expected = pattern.Trim('/').Split('/');
        var actual = path.Trim('/').Split('/');
        return expected.Length == actual.Length
            && expected.Zip(actual).All(p => (p.First.StartsWith('{') && p.First.EndsWith('}')) || p.First == p.Second);
    }

    private static async Task<HttpRequestMessage> SnapshotAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            copy.Content = new StringContent(await request.Content.ReadAsStringAsync(cancellationToken));
            copy.Content.Headers.Clear();
            foreach (var header in request.Content.Headers)
            {
                copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return copy;
    }
}
