using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Yarp.ReverseProxy.Forwarder;

namespace NewStore.Gateway.Tests;

/// <summary>
/// The gateway in the test server at http://localhost. Its sign-in talks to the real Keycloak and its sessions live in the
/// real Redis. The destinations are not real: every forwarded request ends in <see cref="Stub"/>, which records it.
/// </summary>
internal sealed class GatewayApp : NewStoreAppFactory<Program>
{
    public GatewayApp(GatewayKeycloak keycloak, string redisConnectionString)
    {
        Settings["Oidc:Authority"] = keycloak.Issuer;
        Settings["Oidc:ClientId"] = "gateway";
        Settings["Oidc:ClientSecret"] = "gateway";
        Settings["Oidc:RequireHttpsMetadata"] = "false";
        Settings["ConnectionStrings:Redis"] = redisConnectionString;
        Settings["Services:Catalog"] = "http://catalog.test";
        Settings["Services:Inventory"] = "http://inventory.test";
        Settings["Services:Orders"] = "http://orders.test";
    }

    public StubHttpHandler Stub { get; } = new();

    /// <summary>Extra registrations for one test, applied after the gateway's own.</summary>
    public List<Action<IServiceCollection>> TestServices { get; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IForwarderHttpClientFactory>();
            services.AddSingleton<IForwarderHttpClientFactory>(new StubForwarderHttpClientFactory(Stub));
            foreach (var configure in TestServices)
            {
                configure(services);
            }
        });
    }

    public HttpClient CreateBrowserClient() => CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = false,
        BaseAddress = new Uri(GatewayKeycloak.GatewayOrigin),
    });

    private sealed class StubForwarderHttpClientFactory(StubHttpHandler stub) : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(stub);
    }
}
