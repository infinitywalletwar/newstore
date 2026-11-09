using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace NewStore.Messaging.Telemetry;

/// <summary>
/// The library's <see cref="ActivitySource"/>, its <see cref="Meter"/> and the two spans it starts. The name starts with
/// <c>NewStore.</c>, so <c>AddServiceDefaults</c> collects it. Both methods return null when nothing listens.
/// </summary>
internal static class MessagingTelemetry
{
    public const string SourceName = "NewStore.Messaging";

    public static readonly ActivitySource Source = new(SourceName);

    private static readonly Meter s_meter = new(SourceName);

    /// <summary>Handler failures that will be tried again without a limit, per topic. A rising count means a dependency is down.</summary>
    public static readonly Counter<long> TransientRetries = s_meter.CreateCounter<long>(
        "messaging.handler.transient_retries", description: "Handler attempts that failed transiently and will be retried.");

    /// <summary>
    /// <c>publish &lt;topic&gt;</c>, a child of the request that enqueued the event. The relay runs long after
    /// that request ended, so the parent comes from the <c>traceparent</c> stored in the outbox row.
    /// </summary>
    public static Activity? StartPublish(string topic, Guid messageId, string? traceParent) =>
        Start($"publish {topic}", ActivityKind.Producer, topic, messageId, traceParent);

    /// <summary><c>process &lt;topic&gt;</c>, a child of the publish span whose id travelled in the message's <c>traceparent</c> header.</summary>
    public static Activity? StartProcess(string topic, Guid messageId, string? traceParent) =>
        Start($"process {topic}", ActivityKind.Consumer, topic, messageId, traceParent);

    private static Activity? Start(string name, ActivityKind kind, string topic, Guid messageId, string? traceParent)
    {
        // No parseable parent: start a new trace instead of hanging the span under whatever is current.
        var parent = ActivityContext.TryParse(traceParent, null, out var context) ? context : default;
        var activity = Source.StartActivity(name, kind, parent);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", topic);
        activity?.SetTag("messaging.message.id", messageId.ToString());
        return activity;
    }
}
