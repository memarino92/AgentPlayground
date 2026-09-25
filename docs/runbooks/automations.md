# Agent automations

Ask Chat to create an automation, for example: “Every day, get the current date and time and save it as a report.” The agent discovers `automation_catalog`, creates a bounded JSON recipe with `save_automation`, and returns a link to `/automations`. Saving means scheduled, not completed. Use `inspect_automation` or the dashboard to inspect results.

Supported actions: `text` (literal/template), `tool` (clock, scoped coaching search, owner work-journal search), `save_report`, and `notify`. Strings support `{{steps.ID}}`, `{{run.id}}`, and `{{run.scheduledAt}}`. An optional `when: {"step":"earlier_id","equals":"exact output"}` skips a step when the comparison fails. No model chooses steps during execution. Search may use the existing embedding provider; this is not an entirely model-free retrieval guarantee.

Limits: 20 sequential steps, 32 KiB source, 64 KiB output per step, 60 seconds per step. No shell, arbitrary C#, arbitrary HTTP requests, recursive automations or general expression evaluation. The next slice will add isolated generated programs separately.

## Scheduling and revisions

Omit `executeAt` to run as soon as the five-second reconciler picks it up; otherwise provide an offset-bearing ISO timestamp. Optional `repeatEvery` accepts fixed ISO durations from PT1M to P366D. P1D means 24 elapsed hours, not local-calendar daily execution across DST. Runs do not overlap within one definition. Missed intervals are skipped; one overdue occurrence runs before the next future interval.

Each run pins an immutable revision. Updating requires `expectedVersion`, creates a new revision, and replaces future source/timing; it does not change running or historical executions. Run now is independent of the stored schedule and rejects overlap. Pause stops future dispatch; already queued/active work can finish. Resume restores the retained schedule, or schedules now if a one-off had no future run. Failed or revoked runs pause the definition. Inspect and revise before resuming; there is no automatic retry of a terminal run.

## Authorization and privacy

Automation tools default to Owner only. Explicitly enabling Coach access still requires current profile assignments and per-action permissions. Creation and every execution step resolve current database sign-in policy; a saved role is not a grant. Owners see their subject's automations; coaches only their own automations for currently assigned subjects. Only the original author can replace source. API endpoints require internal API key plus signed actor.

Source, outputs, reports and errors are accessible only through scoped APIs. Telemetry exports metadata, not source or report contents. Never put credentials into a recipe. Runtime provider credentials continue to live in encrypted database configuration.

## Persistence and operations

API hosts the saga and step consumers; this retains the API's existing tool/provider/authorization boundary. MassTransit remains on **8.5.8**. EF Core 10 and Npgsql 10 dependencies are pinned centrally. On API startup, EF migrations apply only to the new `automation` schema, with a dedicated migrations history table. Do not call EnsureCreated on the shared application database.

The definition/schedule row is locked while creating a run. Run rows, pinned steps, schedule advancement and start-command outbox intent commit together. Saga result handling commits report domain writes, step outputs, saga progress and next-step/notification intent using the same scoped EF context and consumer outbox. Raw SQL extensions must use this connection/transaction. Do not publish through singleton IBus inside these transactions. Existing Npgsql outboxes and services are unchanged.

Notifications are committed to the existing push-delivery path. A completed `notify` step means queued, not confirmed push acceptance or device delivery. External push delivery retains its existing retry semantics and can duplicate an uncertain send. Pure read step computation can also repeat after interruption; saved report writes are guarded by saga state and unique run/step identity.

Persisted outbox messages survive API restart. A run with no progress for five minutes is failed and its definition paused. Inspect MassTransit error queues and logs for infrastructure faults. Backups must include `automation` and its outbox/history tables; restored pending work can execute, so keep restored environments isolated as with existing scheduled jobs.

## Visibility and verification

Dashboard lists 50 definitions/runs per page, all immutable source revisions, upcoming projections, outputs, saved reports and the starting trace ID. Times are explicitly UTC. Refresh is automatic every ten seconds and available manually.

OpenTelemetry source/meter: `PersonalAgent.Automations`. Spans: `automation.start`, `automation.execute_step`, `automation.commit_step`; approved tags include run ID, step index, action and status. Metrics: `automation.runs`, `automation.step.duration` with bounded action/status labels. Correlate structured server logs with run ID and step index. Error events 4301 (terminal run), 4302 (step), and 4303 (reconciler) flow through the existing metadata-only Sentry logger with trace correlation. Export/Sentry remain controlled by existing live database settings; a disabled exporter does not prevent execution. Metrics can count attempted transitions again if a transaction retries; the database/dashboard is authoritative for completed runs.

Run `dotnet test PersonalAgent.Api.Tests/PersonalAgent.Api.Tests.csproj --filter FullyQualifiedName~AutomationTests` and the Web component tests. With the isolated synthetic stack running, execute `pwsh -NoProfile -File scripts/tests/Test-Automations.ps1`. Its exact `demo automation` chat phrase exercises real function calling and persistence with a fixed synthetic provider, not live model reasoning. It leaves a paused sample for browser inspection.
