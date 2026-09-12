# 0019: Shared service telemetry and OpenInference

- Status: Accepted; service-side implementation complete
- Recorded: 2026-09-12
- Decision date: 2026-09-12, maintainer requested implementation
- Evidence: maintainer request; existing runtime Sentry integration and observability roadmap

## Context

API, Web and Worker already share live-configured, metadata-only Sentry error reporting. Distributed tracing and AI operation visibility are missing. Private coaching and journal content must remain excluded.

## Decision

Extend the shared integrations library with OpenTelemetry HTTP, PostgreSQL and MassTransit traces and runtime/service metrics. Use OpenInference attributes on explicitly instrumented AI operations. Keep Sentry as the sole error-reporting owner and add span correlation. OTLP export is deployment-configured and requires an explicit endpoint; existing Sentry settings retain live reload. Logs exported through OTLP contain only category/event/error type metadata, never formatted application messages or scopes.

## Alternatives

- Global Sentry host initialization would compete with replaceable clients and change the live settings contract.
- Parallel vendor AI instrumentation risks duplicate model/tool spans and content capture.
- Capturing raw prompts, exceptions, SQL and log values provides richer debugging but violates the existing private-content boundary.

## Consequences

One trace pipeline serves any OTLP backend, including OpenInference-compatible backends. Deployment owns endpoint credentials, sampling and retention. Explicit spans must be maintained as AI entry points change. Mobile needs separate native lifecycle integration.

## Delivery and verification

Implemented shared API/Web/Worker traces, metrics and metadata logs; explicit OpenInference operations; native Sentry trace/span context; and nullable traceparent persistence in the custom coaching outbox. Its existing idempotent schema initializer adds the column. Existing rows remain deliverable. Automated tests cover redaction, exporter outage, correlation, usage absence, and retry after the origin ends. See [operations](../runbooks/observability.md) and OPS-01/02/03 for remaining deployed ingestion, dashboard/alert and client-side coverage.

Sources checked 2026-09-12: [OpenTelemetry .NET exporters](https://opentelemetry.io/docs/languages/dotnet/exporters/), [MassTransit observability](https://masstransit.io/documentation/configuration/observability), [OpenInference conventions](https://arize-ai.github.io/openinference/spec/semantic_conventions.html).
