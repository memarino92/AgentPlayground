# 0020: Database-owned OpenTelemetry settings with live export reload

- Status: Accepted; implementation authorized 2026-09-12
- Recorded: 2026-09-12
- Decision date: 2026-09-12
- Evidence: maintainer requested database-first, live-reloaded telemetry and no new environment variables unless database-first is impossible
- Supersedes: deployment-owned exporter and sampler configuration in [0019](0019-observability.md)

## Context

The service instrumentation exists, but the maintainer has no external OTLP backend and uses Railway logs. Observability is primarily a demonstration feature. Requiring more deployment variables conflicts with the desired configuration model.

## Decision

Use the encrypted integration revisions and save/apply/reload flow for OpenTelemetry, isolated from Sentry under `otel`. Add a Settings section for enablement, individual trace/metric/log export, collector base URL, protocol, encrypted authentication headers, root trace sampling and bounded export timeout. Export starts disabled; traces are selected by default, logs and metrics are not. No destination is deployed or activated by this change.

These fields are owned by the database; old OTLP endpoint/header/protocol and sampler environment settings no longer select them. There are no new bootstrap variables. Reuse existing database and encryption bootstrap. A missing database fails closed for export; transient read/validation/replacement failures retain the previous accepted configuration.

Maintain one host collection pipeline. Forward batch exports through an atomic replacement bundle and use a reloadable parent-based sampler. Exporter-only SDK providers attach service resources and own disposal without registering instrumentation sources or meters. Serialize swap against in-flight export so an old exporter cannot be disposed while used. New sampling decisions apply to new spans; existing spans preserve their prior decisions. Buffered signals use the destination active when their batch exports. Disabled signals discard batches. Sampling-based AI metrics remain sampled observations, not billing totals.

Each service reconciles the durable active pointer every 15 seconds and acknowledges its own applied revision. API supports immediate reload. Applying acknowledges configuration installation, not successful remote ingestion. Ordinary batched export failures do not roll back valid settings or fail application work.

## Alternatives

- Rebuild all instrumented providers on every change: risks duplicate listeners, gaps and reset metrics.
- Reload only IConfiguration/IOptions: existing exporters do not automatically adopt new destinations or authentication.
- Deploy a collector now: unnecessary before the maintainer chooses a backend; Sentry accepts direct OTLP traces and Phoenix/Aspire are possible demonstration destinations.

## Consequences

Secret headers stay encrypted and are omitted from read responses. Existing administrator and signed-actor authorization apply. Private collector destinations are allowed intentionally; URL credentials/query/fragment and invalid protocols/headers are rejected, and authenticated destinations require HTTPS. Only trusted deployment administrators can select the collector.

SDK/resource configuration not exposed here remains startup-bound. Live reload is not globally atomic across replicas. Services normally reconcile within one polling interval, with additional time for bounded in-flight export and database operations.

## Delivery and verification

Implemented Settings UI and authorized endpoints, isolated encrypted revisions, per-service reconciliation, live exporter/sampler replacement and independent signal controls. Tests cover lifecycle, failure retention, redaction, authorization, database persistence, real OTLP signal/resource delivery and destination rotation against a local synthetic collector. Hosted backend choice and live ingestion remain unconfigured. See [runbook](../runbooks/observability.md).

Source: [OpenTelemetry exporter lifecycle](https://github.com/open-telemetry/opentelemetry-dotnet/blob/core-1.18.0/src/OpenTelemetry/BaseExporter.cs), [OTLP trace exporter resource ownership](https://github.com/open-telemetry/opentelemetry-dotnet/blob/core-1.18.0/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/OtlpTraceExporter.cs).
