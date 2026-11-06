using System.Diagnostics;

using Microsoft.Extensions.DependencyInjection;

using OpenTelemetry;
using OpenTelemetry.Trace;

namespace NewStore.Messaging.Tests;

public class TelemetryTests(PostgresServer postgres, KafkaServer kafka)
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(30);
    private static readonly ActivitySource s_requests = new("Messaging.Tests.Requests");

    [Fact]
    public async Task Publish_and_process_spans_share_the_request_trace()
    {
        var scenario = await Scenario.CreateAsync(postgres, kafka);
        var probe = new HandlerProbe();
        // The same in-memory exporter a service would have through AddServiceDefaults, built here for just two sources.
        var spans = new SpanCollection("");
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource("NewStore.Messaging")
            .AddSource(s_requests.Name)
            .AddInMemoryExporter(spans)
            .Build();
        using var host = await scenario.StartHostAsync(
            configure: b => b.AddHandler<ThingCreated, CreateThingHandler>(scenario.Topic),
            services: s => s.AddSingleton(probe));
        var created = ThingCreated.New();

        // The "request": a span is current while the event is enqueued, as it is inside an endpoint.
        using var request = s_requests.StartActivity("request");
        Assert.NotNull(request);
        await Scenario.CreateThingAsync(host, created, scenario.Topic);
        request.Stop();

        await Scenario.EventuallyAsync(
            () => Task.FromResult(probe.Completed(created.EventId)), s_wait, "the consumer handled the event");
        await Scenario.EventuallyAsync(
            () => Task.FromResult(InTrace(spans, request).Any(a => a.Kind == ActivityKind.Consumer)), s_wait, "the process span was exported");

        var inTrace = InTrace(spans, request);
        var publish = Assert.Single(inTrace, a => a.Kind == ActivityKind.Producer);
        var process = Assert.Single(inTrace, a => a.Kind == ActivityKind.Consumer);
        Assert.Equal($"publish {scenario.Topic}", publish.DisplayName);
        Assert.Equal($"process {scenario.Topic}", process.DisplayName);
        Assert.Equal(request.SpanId, publish.ParentSpanId);
        Assert.Equal(publish.SpanId, process.ParentSpanId);
        Assert.Equal("kafka", process.GetTagItem("messaging.system"));
        Assert.Equal(scenario.Topic, publish.GetTagItem("messaging.destination.name"));
        Assert.Equal(created.EventId.ToString(), process.GetTagItem("messaging.message.id"));
    }

    private static Activity[] InTrace(SpanCollection spans, Activity request) =>
        [.. spans.Snapshot().Where(a => a.TraceId == request.TraceId && a.Source.Name == "NewStore.Messaging")];
}
