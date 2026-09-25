# 0034: Agent-authored deterministic automations

- Status: Accepted; registered-action recipe slice implemented
- Recorded: 2026-09-25
- Evidence: user requested EF Core/MassTransit sagas, scoped to automations, with chat authoring and operational visibility

## Decision

Keep every MassTransit package on 8.5.8 (no licensed 9.x upgrade). Add an API-hosted automation saga repository using EF Core/Npgsql, a dedicated `automation` schema, versioned migrations, and transactional consumer/bus outboxes sharing the same scoped context. Existing services remain unchanged. MassTransit 8.5.8 selects EF Core 10 on our target framework, requiring the shared Npgsql driver to move from 8 to 10; existing raw-SQL services passed regression testing.

Chat authors bounded registered-action JSON recipes, not executable C# or shell code. Definitions have immutable revisions; each run pins a revision. A database reconciler starts due occurrences; fixed interval recurrence skips missed occurrences and avoids overlapping runs. Source and outputs are private actor/subject-scoped data. Current database authorization and tool permissions are checked at authoring and execution.

Pure step computation is delivered to a consumer. Result handling commits step records, report domain updates, saga progress, and outgoing notification/next-step intent in the same EF transaction. Notifications report queue acceptance, not device receipt. External effects retain their existing delivery semantics. No transaction spans messages or external I/O.

## Alternatives

Marten adds document persistence without the same integrated MassTransit EF outbox path. A custom Npgsql saga repository would require maintaining concurrency and transaction integration. Existing Npgsql outboxes remain for existing flows; this decision adds EF for both saga persistence and messaging, not merely to replace an outbox.

## Delivery and verification

Implemented chat catalog/create/revise/list/inspect/control tools, authorized HTTP endpoints, fixed-interval reconciliation, persisted reports, and an automation dashboard with source, version history, scheduled and past runs, step outputs, and trace IDs. All writes use a shared scoped context; the existing singleton tool boundary creates a scope per operation.

PostgreSQL tests cover atomic report/saga/outbox rollback with injected failures, host replacement before outbox delivery, duplicate results, concurrent scheduling, immutable revisions, authorization revocation and isolation. Component tests cover dashboard details, control and clearing private data on denial. The synthetic smoke test exercises actual chat function calls, SQL transport, persistence, recurrence, revisions, and owner isolation. Browser inspection verifies deep linking, run reports and source. This does not evaluate a live model's recipe-authoring quality or guarantee external notification delivery.

No arbitrary script execution, cron/calendar recurrence, parallel graphs, or migration of existing services in this slice. See [operations](../runbooks/automations.md).
