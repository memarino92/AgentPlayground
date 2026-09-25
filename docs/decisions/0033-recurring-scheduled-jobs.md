# 0033: Fixed-interval recurring scheduled jobs

- Status: Accepted
- Recorded: 2026-09-24
- Decision date: 2026-09-24
- Evidence: `SchedulingService`, `ScheduledJobExecutionService`, `ScheduledJobStore`, agent scheduling tools, and Scheduled Jobs dashboard tests
- Supersedes / superseded by: extends [0021](0021-scheduled-jobs.md), [0022](0022-scheduled-notifications.md), and [0030](0030-streaming-chat-and-agent-job-management.md)

## Context

The application already supported durable one-time agent tasks and notifications, including current authorization checks, conservative recovery, cancellation, management tools, and a Web dashboard. It had no recurrence semantics, so the agent could not create a durable repeating job and the dashboard could not distinguish a series from one-time work.

Recurring execution must retain the existing safeguards. In particular, a restart after an ambiguous external effect must not cause another run, revoked access must stop future work, and stale transport deliveries must not advance a series twice.

## Decision

Add an optional fixed elapsed-time interval to the existing job record. The agent scheduling tools and signed scheduling endpoints accept `repeatEvery` as an ISO-8601 duration from one minute through 366 days. The first run still uses exactly one existing timing input: `delay`, `executeAt`, or `when`.

A series keeps one stable job ID. Every run receives a distinct result conversation and attempt number. After a successful run, API atomically records the completed attempt and advances the authoritative job to its next future due time. Missed intervals are skipped rather than replayed in a burst. Authorization is re-evaluated for every run. Blocked, failed, cancelled, or ambiguous `NeedsReview` outcomes stop the series; only successful completion advances it.

The dashboard labels recurring jobs, shows their interval and next run, retains execution history, and cancels the entire pending series. This slice does not add cron expressions, calendar rules, pause/resume, occurrence limits, or wall-clock/DST recurrence.

## Alternatives

Cron or RFC 5545 recurrence would express richer calendars but introduces timezone, daylight-saving, validation, and missed-run policy that this slice does not need. Creating a new child job for each occurrence would make cancellation and dashboard identity harder and could leave an unbounded chain of records. Transport-native recurrence alone was rejected because application storage must remain authoritative for authorization, cancellation, recovery, and visibility.

## Consequences

Fixed intervals are predictable and preserve the current single-job locking and reconciliation model. A daily interval means 24 elapsed hours, not “the same local clock time every day”; users needing calendar semantics must wait for the broader schedule-manager slice. A failed or uncertain occurrence requires explicit review and does not silently resume later occurrences.

## Delivery and verification

API and Web implementations include recurrence validation, successful-run advancement, distinct occurrence sessions, retained attempts, series cancellation, and dashboard presentation. Regression tests cover duration bounds, two completed occurrences under one job ID, no early repeat, and the recurring dashboard state. Operational behavior is documented in [scheduled jobs](../runbooks/scheduled-jobs.md); richer schedule management remains in roadmap item `FLOW-06`.
