using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using NewStore.ServiceDefaults.Security;

namespace NewStore.Security.Tests;

/// <summary>
/// The smallest service that uses <c>AddNewStoreAuth</c>: one endpoint per policy. <c>/me</c> needs <see cref="Policies.SignedIn"/>,
/// <c>/orders-only</c> needs <c>Client("orders")</c> and <c>/inventory-only</c> needs <c>Client("inventory")</c>.
/// Dispose it to stop the app.
/// </summary>
internal sealed class TestApp : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestApp(WebApplication app) => _app = app;

    /// <summary>A client for the app. <c>WithBearer</c> changes its header, so one test can try several tokens.</summary>
    public HttpClient Client => field ??= _app.GetTestClient();

    /// <summary>Validates with <see cref="TestTokens"/>' key, so no Keycloak is needed.</summary>
    public static Task<TestApp> StartWithTestKeyAsync(string audience = "inventory") =>
        StartAsync(new Dictionary<string, string?>
        {
            ["Auth:Issuer"] = TestTokens.Issuer,
            ["Auth:Audience"] = audience,
            ["Auth:UserClients:0"] = "gateway",
        }, useTestKey: true);

    public static async Task<TestApp> StartAsync(Dictionary<string, string?> settings, bool useTestKey = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddNewStoreAuth()
            .AddClientPolicy("orders")
            .AddClientPolicy("inventory");
        if (useTestKey)
        {
            builder.Services.UseTestSigningKey();
        }

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/me", (System.Security.Claims.ClaimsPrincipal user) => user.UserId()).RequireAuthorization(Policies.SignedIn);
        app.MapGet("/orders-only", () => "ok").RequireAuthorization(Policies.Client("orders"));
        app.MapGet("/inventory-only", () => "ok").RequireAuthorization(Policies.Client("inventory"));
        await app.StartAsync();
        return new TestApp(app);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}
