using System.Net;
using System.Text.RegularExpressions;

namespace NewStore.Gateway.Tests;

/// <summary>
/// Plays the browser: one cookie container for the gateway and Keycloak, and redirects followed by hand. Gateway URLs go to the
/// test server and everything else goes to the network. <c>System.Net.CookieContainer</c> does not model SameSite, so the raw
/// <c>Set-Cookie</c> headers are kept in <see cref="GatewaySetCookies"/> for a test to read.
/// </summary>
internal sealed partial class Browser
{
    private readonly CookieContainer _cookies = new();
    private static readonly HttpClient Network = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });

    public Browser(HttpClient gateway) => Gateway = gateway;

    /// <summary>The test server's client: no cookie handling and no redirects of its own. A test swaps it to restart the gateway.</summary>
    public HttpClient Gateway { get; set; }

    public List<string> GatewaySetCookies { get; } = [];

    /// <summary>Every header and body the gateway sent, as text. A test searches it for something that must never leave the server.</summary>
    public List<string> GatewayTranscript { get; } = [];

    public CookieCollection GatewayCookies => _cookies.GetCookies(new Uri(GatewayKeycloak.GatewayOrigin));

    /// <summary>Sends the cookies the browser holds for the URL. <paramref name="csrf"/> adds the header the app's own calls carry.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, bool csrf = false, HttpContent? content = null)
    {
        var uri = new Uri(new Uri(GatewayKeycloak.GatewayOrigin), url);
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        if (_cookies.GetCookieHeader(uri) is { Length: > 0 } cookieHeader)
        {
            request.Headers.Add("Cookie", cookieHeader);
        }

        if (csrf)
        {
            request.Headers.Add("X-CSRF", "1");
        }
        var toGateway = uri.Host == "localhost";
        var response = await (toGateway ? Gateway : Network).SendAsync(request, TestContext.Current.CancellationToken);
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var setCookie in setCookies)
            {
                if (toGateway)
                {
                    GatewaySetCookies.Add(setCookie);
                }
                // Keycloak marks its cookies Secure even over http. A browser accepts that on localhost and 127.0.0.1; CookieContainer
                // would hold the cookie back from every http request, so drop the flag. The gateway's own cookies are stored as sent,
                // so a Secure flag on one of them would keep it from being sent back, as in a browser on plain http.
                _cookies.SetCookies(uri, toGateway ? setCookie : SecureFlag().Replace(setCookie, ""));
            }
        }

        if (toGateway)
        {
            GatewayTranscript.Add(response.Headers + response.Content.Headers.ToString()
                + await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        return response;
    }

    public Task<HttpResponseMessage> GetAsync(string url, bool csrf = false) => SendAsync(HttpMethod.Get, url, csrf);

    /// <summary>Follows redirects until a response is not one. The caller can stop earlier by sending the requests itself.</summary>
    public async Task<HttpResponseMessage> FollowAsync(HttpResponseMessage response, Uri from)
    {
        for (var hops = 0; response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest; hops++)
        {
            Assert.True(hops < 10, "Too many redirects.");
            from = new Uri(from, response.Headers.Location!);
            response = await SendAsync(HttpMethod.Get, from.ToString());
        }

        return response;
    }

    /// <summary>
    /// The sign-in a person does: <c>/auth/login</c> to Keycloak's form, the form posted with the user's name and password, and the
    /// redirects back to the gateway. Returns the gateway's last response.
    /// </summary>
    public async Task<HttpResponseMessage> SignInAsync(string username, string returnUrl = "/")
    {
        var start = await GetAsync($"/auth/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var loginPage = await FollowAsync(start, start.RequestMessage!.RequestUri!);
        var html = await loginPage.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(loginPage.StatusCode == HttpStatusCode.OK, $"Keycloak's login page answered {(int)loginPage.StatusCode}: {html}");

        var action = WebUtility.HtmlDecode(LoginFormAction().Match(html).Groups[1].Value);
        Assert.False(string.IsNullOrEmpty(action), "No login form on Keycloak's page.");
        var credentials = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = RealmFile.UserPassword(username),
        });
        var posted = await SendAsync(HttpMethod.Post, action, content: credentials);
        Assert.Equal(HttpStatusCode.Found, posted.StatusCode);
        return await FollowAsync(posted, new Uri(action));
    }

    [GeneratedRegex(";\\s*Secure", RegexOptions.IgnoreCase)]
    private static partial Regex SecureFlag();

    [GeneratedRegex("<form[^>]*id=\"kc-form-login\"[^>]*action=\"([^\"]*)\"")]
    private static partial Regex LoginFormAction();
}
