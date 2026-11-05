# 0003. All telemetry goes through the OpenTelemetry Collector

## Status

Accepted

## Date

2026-11-05

## Context

Every service emits traces, metrics and logs over OTLP (Task 5). Something has to receive them. Phase 4 adds the real backends (Prometheus, Loki, Tempo and Grafana), and the services must not need to change when it does.

## Decision

Services send OTLP only to one OpenTelemetry Collector, at `otel-collector:4317` from containers. The collector's config decides where the data goes. For now it writes to its own log (the `debug` exporter), and Phase 4 swaps in the real exporters.

## Alternatives considered

- **Services export straight to each backend.** One hop fewer. But each service then knows every backend's address, protocol and credentials, and changing a backend means redeploying every service.
- **Services export to the Aspire dashboard.** A good viewer with almost no setup. But it keeps data in memory, it is a development tool and not the production path, and we would learn a pipeline we don't ship. The collector is what we run in the cloud too.

## Consequences

- Changing, adding or dropping a backend is an edit to `deploy/compose/otel-collector/config.yaml`. No service changes.
- The collector can later sample, filter or add attributes in one place.
- It is one more process that must be up. If it is down, the exporter drops each failed batch (retry is off unless `OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY=in_memory` is set), the batch processor drops spans once its queue is full, and the services keep working.
- The collector image has no shell, so Compose can't health-check it. It counts as ready once it is running, and `Everything is ready` in its log is the check.
- The `contrib` image is larger than the core one. We use it because it ships the widest set of exporters, which keeps Phase 4's choices open. Tempo and Loki take OTLP through the core `otlphttp` exporter, so we may be able to move to the smaller image then.
