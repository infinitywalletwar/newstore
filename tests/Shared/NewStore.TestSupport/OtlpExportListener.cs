using System.Diagnostics.Tracing;

namespace NewStore.TestSupport;

/// <summary>
/// Listens to the OTLP exporter's EventSource and counts its warning and error events, so a collector-down
/// test can prove that an export was attempted and failed, not just that nothing crashed. A refused
/// connection is reported as the warning <c>TransientHttpError</c>.
/// </summary>
public sealed class OtlpExportListener : EventListener
{
    private const string ExporterEventSource = "OpenTelemetry-Exporter-OpenTelemetryProtocol";

    private int _failures;

    public int Failures => Volatile.Read(ref _failures);

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == ExporterEventSource)
        {
            EnableEvents(eventSource, EventLevel.Warning);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventSource.Name == ExporterEventSource)
        {
            Interlocked.Increment(ref _failures);
        }
    }
}
