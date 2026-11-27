namespace NewStore.Gateway.Auth;

/// <summary>Bound from the "Gateway" section and checked when the gateway starts.</summary>
public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>
    /// The origins a browser loads the app from, such as <c>http://localhost:8100</c>. A cookie-authenticated WebSocket upgrade under
    /// <c>/api/hubs/</c> is accepted only with one of these as its <c>Origin</c> (ADR 0023). Required, at least one.
    /// </summary>
    public List<string> PublicOrigins { get; set; } = [];

    public DestinationHealthCheckOptions DestinationHealthChecks { get; set; } = new();
}

/// <summary>YARP's active health check of a cluster with more than one destination: Delivery's.</summary>
public sealed class DestinationHealthCheckOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>The time between two probes of <c>/health/ready</c>. Each probe times out after 2 s.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);
}

public static class GatewayOptionsExtensions
{
    /// <summary>
    /// Binds <see cref="GatewayOptions"/>, checks it when the host starts, and returns the bound values for the routes that are
    /// built before the host is.
    /// </summary>
    public static GatewayOptions AddGatewayOptions(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOptions<GatewayOptions>()
            .BindConfiguration(GatewayOptions.SectionName)
            .Validate(o => o.PublicOrigins.Count > 0, "Gateway:PublicOrigins needs at least one origin, such as http://localhost:8100.")
            .Validate(o => o.PublicOrigins.All(IsOrigin),
                "Each of Gateway:PublicOrigins must be an absolute http or https origin without a path, such as http://localhost:8100.")
            .Validate(o => o.DestinationHealthChecks.Interval > TimeSpan.Zero, "Gateway:DestinationHealthChecks:Interval must be positive.")
            .ValidateOnStart();

        return builder.Configuration.GetSection(GatewayOptions.SectionName).Get<GatewayOptions>() ?? new GatewayOptions();
    }

    /// <summary>The value an <c>Origin</c> header is compared with: no trailing <c>/</c>, so <c>http://a/</c> equals <c>http://a</c>.</summary>
    internal static string NormalizeOrigin(string origin) => origin.TrimEnd('/');

    /// <summary>Scheme, host and port only: what a browser sends as <c>Origin</c>.</summary>
    private static bool IsOrigin(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && uri.UserInfo.Length == 0
        && uri.AbsolutePath == "/"
        && uri.Query.Length == 0
        && uri.Fragment.Length == 0;
}
