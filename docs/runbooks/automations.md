# Agent automations

Ask Chat to create an automation, for example: “Every day, get the current date and time and save it as a report.” The agent discovers `automation_catalog`, creates a bounded JSON recipe with `save_automation`, and returns a link to `/automations`. Saving means scheduled, not completed. Use `inspect_automation` or the dashboard to inspect results.

Supported actions: `text` (literal/template), `tool` (clock, scoped coaching search, owner work-journal search), `save_report`, and `notify`. Strings support `{{steps.ID}}`, `{{run.id}}`, and `{{run.scheduledAt}}`. An optional `when: {"step":"earlier_id","equals":"exact output"}` skips a step when the comparison fails. No model chooses steps during execution. Search may use the existing embedding provider; this is not an entirely model-free retrieval guarantee.

Recipe limits: 20 steps (serial unless a supervised workflow explicitly opts into parallel children), 32,768 source characters, 65,536 output characters per registered step, 60 seconds per registered step. No host shell, arbitrary HTTP requests, recursive automations or general expression evaluation. The optional owner-only `csharp` action runs through the Worker with separate limits and a default-disabled permission; see [C# programs](automation-programs.md).

## Supervised workflows

For a request such as “research the library's current API, implement the change, and save a report,” chat plans the dependencies and calls `run_supervised_workflow` with a stable request UUID. The parent run appears in **Automations**. Its research, child job result and report stay available when the originating chat closes. Use `inspect_automation` or open the run dashboard for the latest outcome.

Enable **Run supervised workflow** for the owner, along with the existing permissions required by each step. Public research requires **Research in parallel** and the Tavily search grant. Draft publication requires the platform change grant and a configured [coding runner](platform-coding.md).

- `public_research` runs two to four independent public questions concurrently. It returns bounded findings and source links, including partial results when a branch fails. For quick standalone research, chat can call `research_swarm` directly.
- `coding_job` takes a literal instruction and optional `evidenceSteps` referring to earlier public research or literal text. The runner records and watches one coding child job for the request, including across sandbox attempts. It treats the preceding output as evidence. The request's instruction defines the scope of the change.
- `synthesize` takes a question and a list of earlier `sourceSteps`. It combines those outcomes in a bounded model response, with their available public sources and draft PR links. A final `save_report` step can persist the synthesis.
- Existing authorized recipe steps can save a report, run bounded analysis, or notify when explicitly requested. Reference earlier results with `{{steps.ID}}`.

Mixed workflows run once per explicit request. Research and synthesis use the chat model selected when the owner submits the plan. The coding step records whether a draft was opened or the job failed. A completed parent run can report a failed child alongside the research.

### Durable independent children

Set `parallelChildren: true` to dispatch independent public-research and coding children concurrently. Omit it for existing serial behavior. Dependencies are immutable recipe data: `dependsOn` accepts distinct **earlier** step IDs, `evidenceSteps` automatically makes coding wait for its selected research/text evidence, and a `when` condition also implies a prerequisite. Non-child actions (including synthesis, reports, tools, notifications and programs) are source-order barriers: they wait for all prior steps, and later children wait for the barrier. There is still at most one coding child and one repository writer.

```json
{"parallelChildren":true,"steps":[
  {"id":"research","action":"public_research","arguments":{"questions":["What does the public manual say?","What changed in the public changelog?"]}},
  {"id":"code","action":"coding_job","dependsOn":[],"arguments":{"instruction":"Implement the independently scoped change."}},
  {"id":"report","action":"save_report","arguments":{"title":"Outcome","content":"{{steps.research}}\n{{steps.code}}"}}
]}
```

If coding needs the research, add `"evidenceSteps":["research"]` to its arguments (and optionally `"dependsOn":["research"]` to the step). It will no longer run concurrently with that research. Research output is bounded, untrusted evidence, not authorization or an interpolated coding instruction.

Inspect a run to see each child's stable ID, status, attempt, lease/deadline and terminal output. Ownership is inherited through its persisted run and definition, not message-supplied actor data. On API restart the saga outbox replays committed dispatch/results. The five-second reconciler polls existing coding jobs and requeues only expired eligible child work; it does not restart a live coding job or allocate a new coding budget. Worker restart recovery remains the existing coding job store/outbox's responsibility. Coding cancellation is terminal evidence and is never retried as a fresh job.

Interrupted research can execute again, up to three reserved attempts within five minutes; each retains existing per-question/provider/token/time limits. A 75-second lease exceeds the 60-second consumer deadline and is renewed when a delayed child message claims execution. Recovery locks step rows before checking leases. Superseded attempt completions are ignored; committed terminal outputs and reports integrate once even for distinct duplicate messages. This is **not** exactly-once external provider execution/billing. Child execution failure or grant revocation terminates the parent and marks unfinished steps failed/blocked. Coding cancellation is committed through the outbox and retained by request key, even if the job has not yet been created. Already-started external effects/publication are not rolled back. Pause still affects future schedules only, not active-child cancellation.

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

Persisted outbox messages survive API restart. Serial runs with no progress for five minutes are failed and their definitions paused (coding and Dockerfile actions retain their existing longer deadlines). Opt-in parallel runs use persisted child leases, attempt limits and coding-job deadlines instead of the parent cursor timeout. Inspect MassTransit error queues and logs for infrastructure faults. Backups must include `automation` and its outbox/history tables; restored pending work can execute, so keep restored environments isolated as with existing scheduled jobs.

## Visibility and verification

Dashboard lists 50 definitions/runs per page, all immutable source revisions, upcoming projections, outputs, saved reports and the starting trace ID. Times are explicitly UTC. Refresh is automatic every ten seconds and available manually.

OpenTelemetry source/meter: `PersonalAgent.Automations`. Spans: `automation.start`, `automation.execute_step`, `automation.commit_step`; approved tags include run ID, step index, action and status. Metrics: `automation.runs`, `automation.step.duration` with bounded action/status labels. Correlate structured server logs with run ID and step index. Error events 4301 (terminal run), 4302 (step), and 4303 (reconciler) flow through the shared Sentry logger with operation details and trace correlation. Export/Sentry remain controlled by existing live database settings; a disabled exporter does not prevent execution. Metrics can count attempted transitions again if a transaction retries; the database/dashboard is authoritative for completed runs.

Run `dotnet test PersonalAgent.Api.Tests/PersonalAgent.Api.Tests.csproj --filter FullyQualifiedName~AutomationTests` and the Web component tests. With the isolated synthetic stack running, execute `pwsh -NoProfile -File scripts/tests/Test-Automations.ps1`. Its exact `demo automation` chat phrase exercises real function calling and persistence with a fixed synthetic provider, not live model reasoning. It leaves a paused sample for browser inspection.

### Verification without Docker

The default PostgreSQL fixture still uses Testcontainers/pgvector and existing PR gates. A Docker-less coding sandbox may explicitly set **test-only** `AGENTPLAYGROUND_TEST_POSTGRES` to an isolated local database. This is a database-bootstrap exception, not application configuration: it cannot be database-first because the fixture needs a connection before it can create any schema. The override rejects non-loopback hosts and any database name other than `agentplayground_tests`. Tests destructively recreate schemas; never use application data. Run only the focused suite against that database, not parallel fixture classes sharing one override. Automation tests do not need the vector extension; retrieval tests do.

For an already running disposable local PostgreSQL (example uses local trust authentication, no production credentials):

```bash
AGENTPLAYGROUND_TEST_POSTGRES='Host=127.0.0.1;Port=55432;Database=agentplayground_tests;Username=postgres' \
  dotnet test PersonalAgent.Api.Tests/PersonalAgent.Api.Tests.csproj \
  --filter 'FullyQualifiedName~AutomationTests|FullyQualifiedName~SupervisedWorkflowTests|FullyQualifiedName~ResearchSwarmTests' \
  --logger 'trx;LogFileName=mixed-workflow-automation.trx'
```

The mixed-child restart acceptance uses the **real PostgreSQL SQL transport**, not the in-memory bus used by older automation tests. It includes deterministic provider barriers, process-host cancellation/reconstruction, an expired research lease, queued and Worker-claimed coding identity/budget, inbox-delivered out-of-order/stale/duplicate results, dependency evidence and cancellation. A separate SQL replay case stops parent integration after research completion commits and verifies replay without another provider call. Other focused PostgreSQL regressions cover cancellation before coding creation, claim/recovery locking, grant revocation, attempt/deadline limits and scheduler fairness. It does not claim abrupt OS-process-kill or live Railway/provider acceptance.
