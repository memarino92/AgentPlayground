# 0037: Coach call saga and executive summaries

- Status: Accepted; implemented
- Recorded: 2026-09-27
- Decision date: 2026-09-27
- Evidence: maintainer request; `CoachCallWorkflowStateMachine`, `CoachCallOutbox`, `CoachCallExecutiveSummaryService`, coach check-ins detail page
- Supersedes: the proposed workflow implementation in [0005](0005-durable-garden-workflows.md) for coach calls; preserves [0009](0009-transcription-outbox.md)

## Context

The Worker previously dispatched processing directly after transcription or speaker review. It generated a mechanical chunk inventory that included unrelated transcript text, and the main coach check-ins page did not display it. Existing completed calls also have that old summary format. The automation subsystem already has a PostgreSQL-backed MassTransit saga repository and EF outbox.

## Decision

Use a persisted API-hosted MassTransit state machine keyed by upload ID. The upload transaction enqueues a start message; the saga sends transcription and processing commands, waits through speaker review, and sends a summary command after processing completes. Worker and API commit directed workflow signals alongside domain status changes in the existing PostgreSQL outbox. UI status events remain separate. The saga's EF outbox commits state and outgoing commands together.

Generate a versioned, role-aware executive summary from utterances with the configured default chat model. Include only the athlete's relevant weekly check-in, coach feedback on lifts, and cues for next week. Ignore unrelated conversation and omit unsupported categories. Persist the rendered summary and structured JSON in the existing session columns. A periodic reconciler enqueues missing versioned summaries for completed historical calls and creates workflows for unfinished calls that predate this change. The main call detail displays the stored summary to authorized viewers.

## Alternatives

Keeping Worker-controlled continuation would leave orchestration split across consumers. Publishing status events to advance the saga was rejected because a new SQL subscriber can miss events published before its queue exists. Direct signals survive host downtime in the transport. Retranscribing historical calls was unnecessary because completed sessions retain utterances.

## Consequences

The API must be running to advance the workflow and summarize calls; durable messages wait while it is down. Summaries invoke the currently configured default model and may vary with that policy. Existing completed calls are backfilled asynchronously; model failures retry without changing transcript or chunks. The provider submission guard and Worker row locks continue to prevent duplicate provider work and final domain writes. No live-model quality claim is made from synthetic tests.

## Delivery and verification

Implemented with an EF migration for saga rows, existing domain outbox signals, versioned summary writes, historical reconciliation, and the coach check-ins detail panel. PostgreSQL tests cover saga transitions, replay across transport restart, and historical summary idempotency. See [roadmap](../plans/roadmap.md) and [transcription operations](../runbooks/transcription-gateway.md).
