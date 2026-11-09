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
/// </summary>
internal static class TestApp
{
    /// <summary>Validates with <see cref="TestTokens"/>' key, so no Keycloak is needed.</summary>
    public static Task<HttpClient> StartWithTestKeyAsync(string audience = "inventory") =>
        StartAsync(new Dictionary<string, string?>
        {
            ["Auth:Issuer"] = TestTokens.Issuer,
            ["Auth:Audience"] = audience,
            ["Auth:UserClients:0"] = "gateway",
            ["Auth:RequireHttpsMetadata"] = "false",
        }, useTestKey: true);

    public static async Task<HttpClient> StartAsync(Dictionary<string, string?> settings, bool useTestKey = false)
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
        return app.GetTestClient();
    }
}
