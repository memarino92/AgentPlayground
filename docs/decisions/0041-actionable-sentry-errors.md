# 0041: Send actionable application errors to Sentry

- Status: Accepted; server implementation complete
- Recorded: 2026-10-01
- Decision date: 2026-10-01; maintainer requested fuller Sentry diagnostics while protecting access credentials
- Evidence: maintainer request; `PersonalAgent.Integrations/IntegrationLoggingProvider.cs`, `SentryDetails.cs`, and `SentryErrorClient.cs`
- Supersedes: the metadata-only Sentry error boundary in [0010](0010-runtime-integration-settings.md) and [0019](0019-observability.md)

## Context

API, Web, and Worker already share a replaceable Sentry client, but every error becomes “Application error (content redacted).” Exception messages and stack locations are discarded. This makes reports difficult to distinguish or investigate. The maintainer accepts sending application details to Sentry, while access credentials must still be protected.

## Decision

Send Error and Critical log messages, their structured properties, exception type/message/stack context, service, release, environment, and trace/span IDs through the existing shared logger. Preserve the message template for issue grouping and mark Critical events fatal. Mask credential-named structured fields and common credential syntax in text before submitting to the SDK. Bound the number and length of fields. Keep request bodies, headers, scopes, and automatically captured AI content out of the event. Retain the live database configuration and the single privately owned client per server host.

## Alternatives

- Keep metadata-only events: preserves the old privacy boundary but leaves issues too vague to diagnose.
- Add a second global Sentry host SDK: risks duplicate events and conflicts with the live client replacement contract.
- Send raw exception objects and all HTTP/AI payloads: offers more data but can disclose credentials embedded in objects and provider requests.

## Consequences

Failures across API, Web, and Worker now carry operation-specific context and stack locations. Application details, including user content present in error logs, can reach Sentry. Masking covers named credentials and common text patterns, but arbitrary unlabeled secrets in a message cannot be identified reliably. Code that handles an exception without logging Error or Critical still produces no Sentry issue. OpenTelemetry traces retain their separate attribute policy.

## Delivery and verification

The shared logger and SDK event are implemented; automation sandbox, coding job, and gateway failure logs now pass their caught exceptions. In-memory SDK transport tests inspect a real envelope and check useful details alongside credential masking. The mobile app and browser JavaScript have no native Sentry client; adding one requires a separately configured client lifecycle. See the [observability runbook](../runbooks/observability.md) and [roadmap](../plans/roadmap.md).
