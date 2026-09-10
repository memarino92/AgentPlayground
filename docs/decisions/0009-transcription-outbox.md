# 0009: Transactional transcription completion

- Status: Accepted; implemented
- Recorded: 2026-09-10
- Evidence: requested crash recovery improvement; transcription and processing consumers

## Context

Worker commits Processing before publishing its next command. Final processing also commits results separately from status and notification. A crash can strand an upload or repeat final writes.

## Decision

Persist outgoing messages in a PostgreSQL outbox in the same transaction as domain changes. Use the existing Npgsql storage, with a Worker dispatcher and stable message IDs. Send processing commands directly to their queue. Serialize completion on the upload row and recheck status and ownership under the lock. Include speaker-review continuation in the same protocol.

## Alternatives

An in-memory outbox cannot survive process loss. Migrating domain persistence to EF solely to use its MassTransit outbox adds a second persistence model; a small typed Npgsql outbox keeps one transaction boundary.

## Consequences

Delivery is at least once: a crash after send but before acknowledgement may resend. Consumer state guards prevent duplicate final writes. API's durable provider job tracking remains the authority for avoiding duplicate transcription submissions, including its existing operator-review rule for ambiguous submissions. Processing holds a row lock while generating embeddings; retries before commit may repeat embedding requests. No provider transaction is claimed.

## Delivery and verification

Implemented in `CoachCallOutbox`, `CoachCallOutboxDispatcher`, the schema initializer, both Worker consumers, and speaker-review continuation. Seven recovery tests cover PostgreSQL rollback, replacement services with cached provider jobs, lost acknowledgements with stable message IDs, concurrent redelivery, subject/session guards, cancellation, terminal failure and speaker review. A real PostgreSQL MassTransit test stops and recreates the host with a queued command, then verifies processing and hosted outbox draining. Tests inject failure at SQL/acknowledgement boundaries; they do not kill an OS process or call a live provider. Existing stranded uploads require explicit reconciliation; this change cannot infer whether historical notifications were delivered.
