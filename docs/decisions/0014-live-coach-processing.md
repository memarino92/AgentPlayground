# 0014: Live coach processing updates

- Status: Accepted; implemented
- Recorded: 2026-09-11
- Evidence: maintainer request for MassTransit transition events and live transcription/chunking pages; decision 0009

## Context

MassTransit commands already control transcription and transcript processing. Web does not consume their completion events, and the upload page stops polling at speaker review. Reloading the admin form can overwrite unsaved role selections.

## Decision

Keep the existing command flow and publish a metadata-only status event through the domain outbox in each committed status transition. Each Web instance has its own temporary SQL subscription and fans invalidations out to active components. Components reread authorized API data on their renderer, coalesce notifications, unsubscribe on disposal, and reconcile periodically to recover missed events. Events are hints to reread, never authoritative page data or authorization grants. Preserve unsaved speaker choices during refresh.

## Alternatives

Polling alone adds latency and traffic. A shared Web queue would deliver each event to only one instance. A separate browser SignalR hub duplicates the existing Blazor Server circuit. Full workflow/saga replacement is independent of this live-update change.

## Consequences

At-least-once or out-of-order notifications are harmless because pages read current state. The singleton relay stores only subscriptions and metadata, not transcripts or user state. API authorization remains authoritative. Periodic reconciliation covers Web downtime and transport interruptions. Processing still uses its existing transaction and retry semantics; status Processing includes chunking, embeddings and summary generation.

## Delivery and verification

Implemented in the API/Worker status transactions, `CoachCallStatusChangedConsumer`, `CoachCallUpdates` and `CoachCallLiveRefresh`. The upload, admin and transcript pages refresh from notifications and reconcile every 30 seconds. Speaker review immediately updates transcript roles and resumes processing; its controls disable once the upload leaves review. Processing includes chunking, embeddings and summary generation; this does not add chunk-by-chunk progress, cancellation, or retry/reprocess buttons.

Verification includes PostgreSQL transition rollback/replay, two independent temporary SQL subscriptions, background component notifications, profile/upload filtering, subscription disposal, draft preservation, first-apply behavior, and continued updates after speaker review. See the [roadmap](../plans/roadmap.md) and [rollout procedure](../runbooks/transcription-gateway.md). Existing command retries and ambiguous-provider-job handling remain unchanged.
