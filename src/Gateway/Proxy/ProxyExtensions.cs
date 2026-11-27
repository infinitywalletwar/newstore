using NewStore.Gateway.Auth;
using NewStore.ServiceDefaults.Security;

using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.LoadBalancing;
using Yarp.ReverseProxy.Transforms;

namespace NewStore.Gateway.Proxy;

public static class ProxyExtensions
{
    private const string Anonymous = "anonymous";

    /// <summary>
    /// Every route is listed here, so a path that is not listed is not forwarded (an internal endpoint such as a service's
    /// <c>/health/ready</c> stays internal). The destinations come from <c>Services:Catalog</c>, <c>Services:Inventory</c>,
    /// <c>Services:Orders</c>, <c>Services:Media</c> and the list <c>Services:Delivery</c>. The services hold every business rule; the
    /// gateway only asks for a signed-in caller.
    /// </summary>
    public static IHostApplicationBuilder AddGatewayProxy(this IHostApplicationBuilder builder)
    {
        var gateway = builder.AddGatewayOptions();
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
            Route("videos-create", "catalog", Policies.SignedIn, "POST", "/api/videos"),
            Route("my-videos", "catalog", Policies.SignedIn, "GET", "/api/me/videos"),
            Route("uploads-create", "media", Policies.SignedIn, "POST", "/api/uploads"),
            Route("uploads-read", "media", Policies.SignedIn, "GET", "/api/uploads/{id}"),
            Route("uploads-complete", "media", Policies.SignedIn, "POST", "/api/uploads/{id}/complete"),
            Route("uploads-abort", "media", Policies.SignedIn, "DELETE", "/api/uploads/{id}"),
            Route("my-uploads", "media", Policies.SignedIn, "GET", "/api/me/uploads"),
            Route("downloads-create", "media", Policies.SignedIn, "POST", "/api/downloads"),
            Route("my-shipments", "delivery", Policies.SignedIn, "GET", "/api/me/shipments"),
            Route("shipments-read", "delivery", Policies.SignedIn, "GET", "/api/shipments/{id}"),
            Route("delivery-warehouse", "delivery", Anonymous, "GET", "/api/delivery/warehouse"),
            // The tracking hub's WebSocket upgrade. Only GET: the client skips negotiate, and POST .../negotiate stays unrouted.
            Route("hubs-tracking", "delivery", Policies.SignedIn, "GET", "/api/hubs/tracking"),
        ];
        ClusterConfig[] clusters =
        [
            Cluster("catalog", services["Catalog"]),
            Cluster("inventory", services["Inventory"]),
            Cluster("orders", services["Orders"]),
            Cluster("media", services["Media"]),
            DeliveryCluster(services.GetSection("Delivery").Get<string[]>(), gateway.DestinationHealthChecks),
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

    /// <summary>
    /// Delivery runs as two instances (<c>delivery</c> and <c>delivery-b</c> in Compose). The gateway takes turns between them, and
    /// with health checks on it probes each one's <c>/health/ready</c> and sends nothing to one that fails. A WebSocket stays on the
    /// instance it opened on, and needs no sticky session because the client skips negotiate (ADR 0023).
    /// </summary>
    private static ClusterConfig DeliveryCluster(string[]? addresses, DestinationHealthCheckOptions healthChecks)
    {
        if (addresses is not { Length: > 0 })
        {
            throw new InvalidOperationException("Services:Delivery:0 is not set.");
        }

        return new ClusterConfig
        {
            ClusterId = "delivery",
            LoadBalancingPolicy = LoadBalancingPolicies.RoundRobin,
            Destinations = addresses.Select((address, i) => (address, i))
                .ToDictionary(d => $"d{d.i}", d => new DestinationConfig { Address = d.address }),
            HealthCheck = healthChecks.Enabled
                ? new HealthCheckConfig
                {
                    Active = new ActiveHealthCheckConfig
                    {
                        Enabled = true,
                        Interval = healthChecks.Interval,
                        Timeout = TimeSpan.FromSeconds(2),
                        Policy = HealthCheckConstants.ActivePolicy.ConsecutiveFailures,
                        Path = "/health/ready",
                    },
                }
                : null,
        };
    }

    private static ClusterConfig Cluster(string id, string? address) => new()
    {
        ClusterId = id,
        Destinations = new Dictionary<string, DestinationConfig>
        {
            ["main"] = new() { Address = address ?? throw new InvalidOperationException($"Services:{char.ToUpperInvariant(id[0])}{id[1..]} is not set.") },
        },
    };
}
