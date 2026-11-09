using System.Net.Http.Headers;

namespace NewStore.TestSupport;

public static class HttpClientExtensions
{
    /// <summary>Sends <c>Authorization: Bearer &lt;token&gt;</c> on every later request of this client, and returns the client.</summary>
    public static HttpClient WithBearer(this HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
