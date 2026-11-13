using NewStore.ServiceDefaults.Security;

using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

namespace NewStore.Gateway.Proxy;

public static class ProxyExtensions
{
    private const string Anonymous = "anonymous";

    /// <summary>
    /// Every route is listed here, so a path that is not listed is not forwarded (an internal endpoint such as a service's
    /// <c>/health/ready</c> stays internal). The destinations come from <c>Services:Catalog</c>, <c>Services:Inventory</c> and
    /// <c>Services:Orders</c>. The services hold every business rule; the gateway only asks for a signed-in caller.
    /// </summary>
    public static IHostApplicationBuilder AddGatewayProxy(this IHostApplicationBuilder builder)
    {
        var services = builder.Configuration.GetSection("Services");
        RouteConfig[] routes =
        [
            Route("videos-list", "catalog", Anonymous, "GET", "/api/videos"),
            Route("videos-read", "catalog", Anonymous, "GET", "/api/videos/{id}"),
            Route("videos-editions", "catalog", Anonymous, "GET", "/api/videos/{id}/editions"),
            Route("channels-read", "catalog", Anonymous, "GET", "/api/channels/{slug}"),
            Route("channels-create", "catalog", Policies.SignedIn, "POST", "/api/channels"),
            Route("editions-create", "inventory", Policies.SignedIn, "POST", "/api/editions"),
            Route("editions-read", "inventory", Policies.SignedIn, "GET", "/api/editions/{id}"),
            Route("editions-copies", "inventory", Policies.SignedIn, "GET", "/api/editions/{id}/copies"),
            Route("editions-update", "inventory", Policies.SignedIn, "PATCH", "/api/editions/{id}"),
            Route("editions-end-sale", "inventory", Policies.SignedIn, "POST", "/api/editions/{id}/end-sale"),
            Route("orders-create", "orders", Policies.SignedIn, "POST", "/api/orders"),
            Route("orders-read", "orders", Policies.SignedIn, "GET", "/api/orders/{id}"),
            Route("orders-pay", "orders", Policies.SignedIn, "POST", "/api/orders/{id}/pay"),
            Route("my-copies", "orders", Policies.SignedIn, "GET", "/api/me/copies"),
        ];
        ClusterConfig[] clusters =
        [
            Cluster("catalog", services["Catalog"]),
            Cluster("inventory", services["Inventory"]),
            Cluster("orders", services["Orders"]),
        ];

        builder.Services.AddReverseProxy()
            .LoadFromMemory(routes, clusters)
            .AddTransforms(context => context.AddRequestTransform(AccessTokenTransform.ApplyAsync));
        return builder;
    }

    private static RouteConfig Route(string id, string cluster, string policy, string method, string path) => new()
    {
        RouteId = id,
        ClusterId = cluster,
        AuthorizationPolicy = policy,
        Match = new RouteMatch { Path = path, Methods = [method] },
        Transforms = [new Dictionary<string, string> { ["PathRemovePrefix"] = "/api" }],
    };

    private static ClusterConfig Cluster(string id, string? address) => new()
    {
        ClusterId = id,
        Destinations = new Dictionary<string, DestinationConfig>
        {
            ["main"] = new() { Address = address ?? throw new InvalidOperationException($"Services:{char.ToUpperInvariant(id[0])}{id[1..]} is not set.") },
        },
    };
}
