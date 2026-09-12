# Observability

API, Web and Worker share OpenTelemetry 1.18 instrumentation and the existing live-configured Sentry error reporter. Sentry remains configured through **Settings**; a received test event verifies that project's ingestion path. Only Error/Critical logs generate application events. An empty issue list does not prove every code path reports failures: handled warnings and successful requests do not create issues.

## Database-first configuration and live reload

Open **Settings → OpenTelemetry** as a deployment administrator. Use the existing database/encryption bootstrap and administrator allowlist; no new environment variables are required.

1. Enter a collector **base URL** and choose `grpc` or `http/protobuf`. For HTTP/protobuf the exporter appends `v1/traces`, `v1/metrics` and `v1/logs` to the base URL.
2. Select the signals your destination accepts. Traces are selected by default; metrics and logs are off, so Railway can remain the normal log viewer. Export itself is disabled until enabled and applied.
3. If needed, replace the encrypted authentication headers using comma-separated `name=value` pairs. Keep leaves the stored value untouched; clear removes it. Headers never return in API/UI read responses. Authentication requires HTTPS; unauthenticated local/private collectors may use HTTP.
4. Choose the trace sample rate and export timeout, then **Validate and save**. This saves an encrypted candidate; it does not change active exporters.
5. **Apply saved revision** promotes it and reloads API. Web and Worker normally reconcile within 15 seconds; refresh to see each instance's applied revision. **Reload active settings** retries API immediately. Database/export operations can add time to the polling interval.

Enable/disable, signal selection, endpoint, protocol, headers, sampling and timeout all reload without a service restart. Invalid candidates are rejected. Failed reads or exporter construction keep the previous accepted configuration and do not disclose credentials. Old `OTEL_EXPORTER_OTLP_ENDPOINT`, protocol/header/timeout and sampler environment variables no longer configure these database-owned fields. Existing Sentry overrides retain their established behavior.

Instrumentation stays attached once. Export bundles switch atomically with respect to in-flight export. Buffered batches use the active destination at export time; disabled signals discard them. Existing spans retain their sampling decisions; the new rate governs new root spans and respects upstream parent sampling. Span-derived AI metrics are sampled observations, not billing totals. Missing provider usage remains absent; no cost estimate is invented.

Service **Applied** status confirms that configuration was installed, not that the remote collector received data. A syntactically valid but unreachable destination still applies; batched export errors do not break requests. Without available bootstrap/database settings, export stays off until a successful reconciliation. Database failures after a working apply retain that configuration.

Service/resource identity and SDK collection intervals remain startup-bound. See [decision 0020](../decisions/0020-live-database-telemetry-settings.md).
## Coverage and privacy

- HTTP server/client and Npgsql spans, MassTransit producer/consumer spans and metrics, runtime metrics, and correlated metadata logs in all three services.
- OpenInference AGENT, LLM, TOOL, RETRIEVER and EMBEDDING spans for chat, journal parsing, tool execution, semantic memory, journal/coach searches and embedding requests. Streaming chat records provider usage updates when available. Synthetic chat uses the same model wrapper.
- AI duration by kind/outcome and input/output token counters. Metric labels exclude actors, sessions and prompt text.
- Sentry events retain service/environment/release, error type and native trace/span context. One existing logger provider owns error submission; no second global Sentry SDK or duplicate AI auto-instrumentation is installed.
- Exported logs omit message values, formatted messages, scopes and exception content. Trace export uses an attribute allowlist, removes event/link attributes and exception descriptions, and normalizes SQL/HTTP names. Prompts, completions, transcripts, tool arguments/results, SQL statements, headers and query strings are excluded. Local console logging remains governed by existing application logging.
- The custom coaching outbox stores only W3C traceparent alongside its message. Dispatch and subsequent retries resume that parent even after the originating process exits. Existing rows without a parent start a new trace. Its existing schema initializer adds a nullable column idempotently; deploy/restart through the normal schema initialization path before dispatching with new code.

## Rehearsal and troubleshooting

1. Run the [synthetic environment](synthetic-demo.md) with a reachable collector selected through Settings. No private content is needed.
2. Start a chat and submit a synthetic coaching recording. In the backend search `service.name` for `PersonalAgent.Api`, `PersonalAgent.Web` and `PersonalAgent.Worker`. Follow HTTP → messaging → agent/tool/retrieval/model spans. Outbox deliveries use `outbox.deliver`.
3. For a Sentry Error/Critical event, copy its `trace_id` into the trace backend and inspect the `span_id`. A trace may be absent because of sampling or retention. The Sentry test action is synthetic and may not have an active trace.
4. If spans are absent, verify endpoint reachability, protocol, collector routing and sampling, then check the active revision and each service acknowledgement. `/health` endpoints are filtered from tracing.
5. Disconnect the collector and repeat a synthetic operation. SDK export is batched and must not fail application requests. Reconnect and verify new telemetry arrives; telemetry dropped during an outage is not a durable audit trail.

Automated coverage includes synthetic privacy fixtures, log and Sentry correlation, missing usage, unreachable exporter behavior, and a PostgreSQL outbox retry after its origin ends. Regression suites cover API, Web, Worker and Contracts. Additional reload checks cover real OTLP export/resource preservation, destination rotation, independent signals, encrypted revisions and masked UI fields. Backend ingestion and an end-to-end deployed trace still require the chosen collector; this change does not create hosted dashboards or alerts.

The Android companion and browser JavaScript do not yet have separate native/client telemetry. Server-side Blazor errors go through Web logging. Startup failures before host logging initializes, process-native crashes, long-term retention, operational alert thresholds and external backend dashboards remain separate work.
