# Roadmap

Recorded 2026-09-07; selectable backlog expanded 2026-09-10. The numbered themes below retain the original suggested sequence; the [selectable backlog](#selectable-backlog) is an unprioritized menu for a predominantly single-maintainer application. Estimates are deliberately omitted until the first slices expose their integration work.

## 1. Reproducible local data and proven recovery — in progress

**Observed:** `infrastructure/backup/backup.sh` uploads custom-format dumps. Snapshot/restore helpers and a synthetic full-stack local environment are implemented; a production-archive application recovery rehearsal remains outstanding. Encrypted config, push tokens, staged audio, and SQL transport share the database.

**Deliver:** snapshot and restore helpers with named source/target settings, exit-code checks, checksums, an immutable manifest, an empty local target, and local-target guards. Separate full recovery from development sanitization. Use local configuration and a local encryption key; create fresh transport infrastructure before starting services. Add a synthetic seed for development without production access. Fix Compose and examples against the seeded startup contract.

**Done when:** a fresh checkout can start all server services using documented local setup; a production-format backup is restored into an isolated target; scoped chat, config decryption, vector search, and controlled background work pass; measured recovery time and actual backup age are recorded. See [recovery runbook](../runbooks/database-recovery.md).

## 2. AI gateway and consistent tool interface — catalog, registry and transcription gateway implemented

**Observed:** journal parsing and embeddings already cross typed bus contracts to API, AssemblyAI adapter/options now live in API, `AgentTaskExecutionService` previously selected `gpt-4o-mini` (now delegates the default to API), and journal schema assumes 1536-dimensional vectors. Tool metadata was duplicated between chat and access services; the shared registry now removes that duplication.

**Deliver in slices:**

1. Keep runtime model discovery behind `GET /api/models` (already consumed by Web); make the source refreshable inside API with cache/fallback and chat-capability policy. Remove vendor defaults from Worker.
2. Define neutral capability contracts and API-local interfaces for transcription, chat task execution, structured extraction, and embeddings. Route by capability/policy rather than vendor model ID in Worker.
3. Move AssemblyAI adapter, HTTP client, secrets, options, vendor response mapping, and provider tests into API. Preserve domain transcript processing in Worker; keep provider job persistence and replay semantics explicit.
4. Persist transcription submission/job IDs; make resume and redelivery idempotent. Avoid a synchronous multi-minute HTTP proxy or binary bus payloads.
5. Build the tool catalog and function list from one registration with server-bound context, authorization, availability, and execution logging.
6. Define an embedding-space/version contract, keeping stored/query vectors compatible. A provider swap with changed dimensions or semantics requires migration/re-embedding; make that API-owned work rather than silently mixing spaces.

**Done when:** swap a fake transcription provider for another by changing API registration/configuration only; shared contracts and Web/Worker/Mobile code remain unchanged; provider SDKs/types/keys do not cross the gateway; role and subject tests pass. No provider-specific packages should remain in clients. See [0004](../decisions/0004-agent-service-boundary.md).

## 3. Android identity and product polish — before broader client distribution

**Observed:** `PersonalAgentApiClient` stores the shared internal key in `Preferences` and accepts a profile selected locally. Mobile registration/approval routes do not use the chat signed-actor filter. The Android manifest permits backups and cleartext traffic. CI tests server projects only.

**Deliver:** establish user/device-scoped credentials through a trusted login/enrollment flow; resolve profile and decision author server-side; remove shared API/signing credentials from the app. Enforce authorization on mobile and approval endpoints. Separate debug HTTP/emulator options from release transport settings. Then polish onboarding, permission denial, token renewal, notification deep links, logout, expired/duplicate approval decisions, network failures, and accessible loading/error states.

**Done when:** device tests cover cold/warm notification launch, correct account routing, logout/re-login, denied permission, offline/reconnect, expired approval, and cross-user rejection. CI builds a release Android artifact without production credentials. Keep WebView authentication in the test matrix; a backend build is insufficient.

## 4. One durable Agent Framework workflow

**Observed:** conversational tool calling, retrieval, scheduling, and manual speaker review exist, but full workflow/session state is not durable. `TranscribeCoachCallConsumer` catches failures and records them, so exceptions do not automatically participate in the normal retry pipeline after that catch.

**Deliver:** migrate the coach pipeline through explicit states with persisted checkpoints and human review. Add restart, redelivery, rejection, timeout, and provider-failure cases. Retain MassTransit for reliable delivery; let the workflow define progress and pause/resume behavior. Verify C# package compatibility first.

**Done when:** an in-flight job survives a process restart and a delayed human decision without duplicate provider submission or final writes. Show its trace and evidence-linked result in a synthetic demo. See [0005](../decisions/0005-durable-garden-workflows.md).

## 5. Portfolio presentation and measured improvement

**Deliver:** a short synthetic-data walkthrough: upload a coach note, attribute speakers, retrieve a cited cue, schedule a follow-up, then show a resumed workflow. Add screenshots, a two-minute demo, an architecture explanation, and a recovery rehearsal result. Publish only synthetic examples, never production journal/transcript excerpts.

Introduce a versioned evaluation set for retrieval relevance, grounded answers, extraction accuracy, authorization, tool errors, latency, and cost. Capture user corrections. Let a review workflow propose prompt/memory/tool-policy improvements; compare against the baseline, approve, deploy, and roll back if results worsen.

**Done when:** a reviewer can understand the problem, run a demo without private data, inspect tests/decisions, and see measurable engineering tradeoffs. Complete the [public-release review](../runbooks/public-release.md) before changing visibility.

### Future plan: Learning Lab — proposed, not implemented

Recorded 2026-09-10 from the maintainer's requested plan. Build an agent that proposes improvements, evaluates them against a fixed baseline, and lets a human approve, reject, or roll them back. This extends the gateway, durable messaging, and review workflows described in [0005](../decisions/0005-durable-garden-workflows.md); it does not imply that durable workflow orchestration or evaluation infrastructure is already complete.

**First capability:** answer coaching questions with supporting transcript evidence. Start with the evaluation baseline and comparison screen, then add automatic proposal generation.

**Demo sequence:**

1. The assistant gives an incomplete answer to a coaching question.
2. The user flags it: "You missed the coach's latest correction."
3. An improvement worker analyzes the failure and proposes a prompt revision with a diff and explanation.
4. Evaluate the current and candidate versions against the same fixed evaluation set.
5. Show changed answers, scores, regressions, latency, and cost in the UI.
6. Let an authorized reviewer approve or reject the candidate and roll back an approved change afterward.

**Proposed storage split:** prompts do not have to live in the database, but immutable database versions fit this review workflow.

| Store | Contents |
| --- | --- |
| Git | Default prompts, evaluation fixtures, scoring rules |
| PostgreSQL | Immutable prompt versions, proposals, evaluation results, approval history, active-version pointer |
| Application code | Authorization, tool permissions, promotion rules |

Each execution records its prompt version, model/settings, and relevant code version. Promotion changes the active-version pointer; rollback restores the previous one. Existing sessions and workflow runs keep their pinned version. Chat instructions in `AgentChatService` and the separate journal parsing prompt are migration considerations; the first slice is limited to coaching answers. Record a proposed persistence/promotion decision before implementation.

**Evaluation and delivery:** version the baseline fixtures and scoring rules; compare evidence grounding and use of the latest correction alongside regressions, latency, and cost. Keep some evaluation cases hidden from the proposer so it cannot simply tailor changes to visible examples. Use synthetic examples for the public demo. The supplied [OpenAI evaluation guidance](https://github.com/openai/evals/blob/main/docs/build-eval.md) is a reference to review when implementing, not a selected tooling dependency.

**Done when:** a reviewer can inspect a correction, prompt diff, reproducible baseline/candidate comparison, and approval history; rejected candidates never become active; authorized promotion and rollback affect new executions while in-flight work retains its pinned version. The demo must show a rejected regression as well as an accepted improvement. The portfolio claim is: "The system detects failures, proposes changes, and demonstrates whether they improve performance."

## Future plan: scheduled-task authorization — proposed, not implemented

Recorded 2026-09-10. Expand the scheduling authorization follow-up into a bounded delivery item, recommended before Learning Lab implementation.

**Observed:** `PersonalAgent.Worker/Services/AgentTaskExecutionService.cs` sends scheduled execution requests through `SendAsOwnerAsync`, with the role fixed to `Owner`.

**Deliver:** persist the trusted originating actor, subject, and authorization context when scheduling work; propagate that context through durable messages and execution. Resolve current permissions server-side when the task runs, including retries, so stored roles cannot preserve revoked access. Remove unconditional Owner execution. Define a migration path for existing schedules with missing actor context; do not silently grant them Owner authority. Record the authorization decision before implementation, extending [0002](../decisions/0002-actor-and-tool-authorization.md).

**Done when:** tests demonstrate authorized execution, Coach restrictions, cross-user rejection, permission revocation between scheduling and execution, forged context rejection, and safe handling of legacy schedules. Denied runs have an inspectable outcome and perform no unauthorized tool action; redelivery cannot bypass the checks.

## Future plan: Sentry and OpenTelemetry/OpenInference observability — proposed, not implemented

Recorded 2026-09-10 at the maintainer's request. Make application failures and agent execution inspectable across Web, API, Worker, and durable messaging, with useful evidence for the Learning Lab.

**Deliver in slices:**

1. Add Sentry error reporting with service, environment, and release context; correlate failures with the affected request or workflow and define actionable alerts.
2. Add OpenTelemetry tracing, metrics, and log correlation across HTTP, MassTransit delivery, background execution, and provider calls. Preserve correlation through retries, outbox dispatch, and resumed work.
3. Evaluate and adopt OpenInference conventions for agent/model/tool/retrieval spans where compatible with the pinned .NET and Agent Framework packages. Record model/settings, duration, token usage and cost when available, tool outcomes, and prompt/evaluation version identifiers when those features exist. Distinguish unavailable usage from zero and estimated cost from measured usage.
4. Verify Sentry/OpenTelemetry interoperability and OpenInference attribute support before selecting SDKs, exporters, or a trace backend. Record the integration decision, sampling/retention choices, and ownership of instrumentation to avoid duplicate spans and error reports.
5. Document local synthetic validation, deployment configuration, and a troubleshooting walkthrough. Keep secrets and private journal/transcript content out of exported telemetry by default; verify redaction with synthetic fixtures. Telemetry export failures must not prevent application work.

**Done when:** a synthetic coaching request can be followed across service and message boundaries; an injected failure produces a correlated Sentry issue with release context; a retry or resumed job remains traceable; agent spans expose useful timing and available usage data. Verify content redaction, exporter-outage behavior, and absence of duplicate instrumentation. Provide an operational view of error rates, latency, queue/outbox delays, and provider failures, with a documented path from an alert to the relevant trace.

## Future plan: in-app integration settings — proposed, Sentry first

Recorded 2026-09-10. The maintainer identified Sentry as the likely next integration and wants to enter required settings in the app, validate them, and reload or apply configuration. See [proposed decision 0010](../decisions/0010-runtime-integration-settings.md).

**Deliver:** a registered, typed settings form with encrypted secret storage, deployment-administrator authorization, immediate field validation, server validation, and explicit save/apply actions. Show configured secrets without revealing them, effective overrides, saved versus running revisions, validation results, and per-service application status. Bootstrap database access and the encryption key remain external. New integration adapters define their supported fields and reload policy; arbitrary keys do not add capabilities.

**First vertical slice:** configure Sentry enabled state, DSN, environment, and supported sampling settings; send a synthetic test event; then show which services applied the revision. Verify SDK lifecycle behavior before promising live reload. Support validated live updates or controlled reinitialization where safe and clearly indicate restart-required settings. Keep the last working configuration on validation/application failure and reconcile services that were offline.

**Done when:** an authorized administrator can configure and test Sentry entirely through the application settings flow after normal deployment bootstrap; invalid or conflicting edits cannot silently replace working settings; secrets never return in read responses; overrides and partial application are visible; supported reload/disable/re-enable paths are tested without duplicate reporting. Implement `CONFIG-01` together with `OPS-01` as the proposed next integration slice; broader provider migration remains separate.

## Selectable backlog

Recorded 2026-09-10. All entries are **unprioritized proposals**, including remaining slices of existing roadmap themes. They are not implementation claims or commitments. This catalog includes the Learning Lab, scheduled authorization, and observability plans above; their detailed descriptions remain authoritative. Existing implementation evidence remains in the progress sections below.

Use the stable IDs to pick work, for example `PRODUCT-01` or `OPS-02`. Categories do not imply delivery order. Each row describes a bounded first slice and an observable acceptance outcome, rather than the entire possible feature. Dependencies describe prerequisites for that slice; investigation can start earlier. Before selecting work, verify the current implementation and narrow scope. Record significant architecture choices using the decision template before implementation.

When an item is selected, add its ID to the selection table and define the exact scope, validation, and exclusions. Update status and evidence here when it lands; do not maintain a separate conflicting status list. Suggested statuses are Proposed, Selected, In progress, Blocked (with reason), Done (with evidence), and Deferred.

| Selected ID | Status | Scope and completion evidence |
| --- | --- | --- |
| None selected from this catalog yet | — | Existing work in progress above retains its recorded status. |

### Everyday product value

| ID | Idea and first slice | Done when | Dependencies / scope notes |
| --- | --- | --- | --- |
| PRODUCT-01 | **Evidence drawer:** open a cited coaching answer beside the exact transcript excerpt, speaker, and timestamp. | Every displayed citation opens the correct authorized source; missing or deleted evidence has an explicit state. | Existing retrieval and transcript data; audio playback optional. |
| PRODUCT-02 | **Coaching timeline:** browse cues and corrections by date, coach, and topic. | A synthetic sequence shows how advice changed and links each entry to its source. | Use existing source records first; inferred relationships require review. |
| PRODUCT-03 | **Weekly reflection draft:** summarize wins, recurring difficulties, and open questions from a chosen week. | The user can edit and save a draft with evidence links; unsupported claims are omitted or clearly flagged. | On-demand first; automated delivery depends on TRUST-01. |
| PRODUCT-04 | **Follow-up inbox:** collect proposed tasks and questions from coaching notes. | The user can accept, edit, dismiss, and trace each suggestion to its source without creating duplicate tasks. | Build on existing task and approval records; TRUST-01 before scheduled execution. |
| PRODUCT-05 | **Goal progress journal:** attach notes and coaching cues to a user-defined goal. | A user can record a goal, link evidence, and review progress without invented completion claims. | Manual links first; automatic suggestions later. |
| PRODUCT-06 | **Session preparation brief:** prepare a short agenda before a coaching call. | The brief contains recent corrections, unresolved questions, and relevant sources that the user can edit. | Existing records; start on demand. |
| PRODUCT-07 | **Unified search:** search journals, transcripts, and conversations with date/source filters. | Results enforce subject access and open the right source; empty states and pagination work. | MEMORY-02 can improve ranking later. |
| PRODUCT-08 | **Export a useful artifact:** turn a selected coaching recap into Markdown with source references. | Preview and export contain only selected, authorized content and preserve useful citations. | Start with Markdown; other formats are separate slices. |

### Memory and answer quality

| ID | Idea and first slice | Done when | Dependencies / scope notes |
| --- | --- | --- | --- |
| MEMORY-01 | **Memory inspector:** view what the assistant remembers and correct or retire an entry. | Edits preserve provenance; retired entries stop appearing in retrieval; other users cannot inspect them. | Define source versus derived-memory ownership; schema changes depend on DATA-01. |
| MEMORY-02 | **Retrieval comparison:** compare current vector retrieval with lexical/hybrid alternatives on fixed cases. | A report shows relevance, grounded-answer impact, and latency using the same authorized corpus. | LAB-01; adopt a ranking change only if results support it. |
| MEMORY-03 | **Latest-correction handling:** detect potentially superseded coaching cues. | A user can review a suggested supersession; answers prefer the approved correction while retaining history. | PRODUCT-02 or equivalent provenance; LAB-01 for conflicting/date-sensitive cases. |
| MEMORY-04 | **Ingestion quality preview:** inspect extracted topics, speaker attribution, and likely duplicates before accepting derived knowledge. | Corrected extraction can be saved once and replay cannot recreate rejected entries. | Existing speaker review; coordinate with FLOW-01. |
| MEMORY-05 | **Embedding-space versioning:** record the embedding model/space and support a controlled re-embedding run. | Incompatible stored/query vectors are rejected; a resumable migration validates results before switching. | Existing gateway plan; DATA-01. |
| MEMORY-06 | **Insufficient-evidence behavior:** ask a clarifying question or explain missing evidence instead of filling gaps. | Fixed ambiguous and unsupported questions produce useful, grounded responses without fabricated citations. | LAB-01; evaluate usefulness as well as refusal rate. |

### Trust and user control

| ID | Idea and first slice | Done when | Dependencies / scope notes |
| --- | --- | --- | --- |
| TRUST-01 | **Scheduled-task authorization:** preserve the originating actor and recheck access when work executes. | Revocation, Coach restrictions, cross-user attempts, retries, and legacy schedules pass the detailed plan above. | Extend decision 0002; migrate existing schedules explicitly. |
| TRUST-02 | **Action preview and approval:** show the exact proposed external action before a tool performs it. | Approval binds to actor, subject, action arguments, and expiry; changed or replayed approvals cannot authorize a different action. | Existing tool registry; explicit policy enforcement beyond descriptive metadata. |
| TRUST-03 | **Access explanation:** show an owner which tools and subjects a role can access and why. | The UI agrees with execution-time authorization, including a recently revoked permission. | Existing registry and permission UI; avoid duplicating policy rules. |
| TRUST-04 | **Activity history:** show important configuration, permission, approval, and action changes. | An authorized user can inspect who did what and when, including failed actions, with sensitive values excluded. | DATA-01 for new audit persistence; distinguish audit records from sampled telemetry. |
| TRUST-05 | **Untrusted-content evaluation:** test malicious instructions embedded in transcripts or retrieved documents. | Cases measure whether source content can override role limits, expose another subject's data, or trigger unauthorized tools. | LAB-01 and existing authorization; add fixes based on observed failures. |
| TRUST-06 | **Device identity and revocation:** replace shared Android credentials with trusted user/device enrollment. | A revoked device loses access and cannot select another profile; release builds contain no shared API/signing secret. | Existing Android roadmap; prerequisite for broader mobile distribution. |

### Durable work and recovery

| ID | Idea and first slice | Done when | Dependencies / scope notes |
| --- | --- | --- | --- |
| FLOW-01 | **Durable coaching workflow:** persist checkpoints around transcription, speaker review, and final processing. | Restart during human review resumes once after an authorized decision without duplicate submission or final writes. | Decision 0005; verify pinned framework APIs; reuse implemented outbox. |
| FLOW-02 | **Job activity screen:** display running, waiting, failed, and completed jobs with next actions. | The user can see why a job is waiting and open its evidence or review step without raw infrastructure details. | Existing persisted jobs first; full timeline expands with FLOW-01. |
| FLOW-03 | **Safe retry and reconciliation:** resolve ambiguous transcription jobs through an operator workflow. | An ambiguous job can be inspected and reconciled without blindly submitting another paid provider job. | Existing transcription job persistence; explicit provider evidence and authorization. |
| FLOW-04 | **Cancellation and expiry:** cancel queued work and expire stale human-review requests. | Cancellation prevents subsequent side effects where still possible and clearly reports work already committed. | FLOW-01 and TRUST-01 for scheduled work; define races explicitly. |
| FLOW-05 | **Process-kill recovery rehearsal:** interrupt real service processes at defined workflow boundaries. | Restart preserves pending work and avoids duplicate domain effects, with repeatable synthetic evidence. | Existing outbox and job persistence; broader checkpoint cases follow FLOW-01. |
| FLOW-06 | **Schedule manager:** inspect upcoming runs, pause a schedule, and explain missed-run behavior. | Time zone and daylight-saving cases, pause/resume, and duplicate delivery have explicit tested outcomes. | TRUST-01; reuse current scheduler before considering replacement. |

### Observability and operating cost

| ID | Idea and first slice | Done when | Dependencies / scope notes |
| --- | --- | --- | --- |
| CONFIG-01 | **In-app integration settings:** enter, validate, save, and apply typed integration configuration with encrypted secrets. | Sentry setup and a synthetic test event work through the UI; saved/applied revisions, overrides, failure, and restart requirements are accurate per service. | Proposed decision 0010; DATA-01 for revision persistence; deliver alongside OPS-01 first. |
| OPS-01 | **Sentry integration:** capture application errors with service, environment, and release context, configured through the app. | An injected synthetic failure produces a useful issue with private content excluded and no duplicate report. | CONFIG-01; detailed observability plan above; verify SDK/exporter and reload lifecycle choices before implementation. |
| OPS-02 | **OpenTelemetry across services:** correlate HTTP, SQL transport, outbox, and background execution. | A synthetic operation can be followed across boundaries and retries, and exporter failure does not break work. | Detailed observability plan; integrate error correlation with OPS-01. |
| OPS-03 | **OpenInference agent traces:** describe model, retrieval, and tool operations consistently. | Compatible spans expose timing, outcomes, and available usage without exporting private prompts by default. | OPS-02; verify .NET/Agent Framework and backend attribute support. |
| OPS-04 | **Usage and budget controls:** report usage by capability and set a limit for new provider work. | Concurrent work cannot bypass the defined budget policy; unknown usage and cost estimates are explicit. | Durable accounting and reservation policy; sampled traces alone are insufficient. |
| OPS-05 | **Operational dashboard and alerts:** show failures, latency, stale jobs, and outbox delay. | A synthetic failure or stalled job triggers an actionable alert linked to a troubleshooting path. | OPS-01/02 plus job state; define thresholds from a baseline. |
| OPS-06 | **Provider outage behavior:** bound retries, apply backoff, and expose recoverable failure states. | Simulated timeout/rate-limit/outage cases stop within limits and resume safely without duplicate external effects. | Existing gateway/job behavior; preserve ambiguous-submission review. |

### Data and delivery foundations

| ID | Idea and first slice | Done when | Dependencies / scope notes |
| --- | --- | --- | --- |
| DATA-01 | **Versioned database migrations:** establish explicit schema history and startup ownership. | Fresh and existing databases reach the expected version; concurrent startup and partial failure are handled deliberately. | Existing deferred work; prerequisite for larger persistence changes. |
| DATA-02 | **Full application recovery rehearsal:** restore a production-format archive into an isolated target. | Scoped chat, configuration decryption, vector search, and controlled background work pass with measured recovery time and backup age. | Existing recovery runbooks; synthetic startup alone does not close this item. |
| DATA-03 | **Backup manifests and freshness monitoring:** attach checksums/version metadata and report stale or failed backups. | Corrupt/missing archives are detected and a stale-backup condition is observable; retention behavior is documented. | Existing backup uploader; OPS-05 can host alerts later. |
| DATA-04 | **Retention and deletion:** define how source audio, transcripts, derived memory, and telemetry expire. | A preview identifies affected records and authorized deletion removes active derived retrieval data; backup retention limits are explicit. | DATA-01; policy decision before destructive implementation. |
| DATA-05 | **Release confidence:** extend CI with reproducible service smoke checks and an Android release build. | A clean build exercises the synthetic server flow and produces a mobile artifact without production credentials. | Extend existing CI rather than duplicating it; record remote results. |
| DATA-06 | **Focused maintainability work:** split endpoint groups or consumer persistence while implementing a related feature. | The selected capability has clear ownership and existing route/auth/replay behavior remains covered. | Couple to a selected feature; no repository-wide rename or speculative rewrite. |

### Learning Lab and portfolio

| ID | Idea and first slice | Done when | Dependencies / scope notes |
| --- | --- | --- | --- |
| LAB-01 | **Coaching evaluation baseline:** version questions, transcript fixtures, expected evidence, and scoring rules. | Repeatable runs report groundedness, latest-correction handling, regressions, latency, and available cost, with recorded configuration. | First Learning Lab slice; synthetic provider smoke tests are not model-quality evidence. |
| LAB-02 | **Candidate comparison screen:** compare two manually supplied prompt candidates. | A reviewer sees prompt diffs, changed answers, score changes, and regressions on identical fixtures. | LAB-01; no automatic proposer needed. |
| LAB-03 | **Immutable prompt versions and promotion:** approve, reject, and roll back a candidate. | New runs use the active version; existing sessions stay pinned; history records authorized transitions. | LAB-02, DATA-01, and a persistence/promotion decision. |
| LAB-04 | **Correction-driven proposal worker:** turn user feedback into a candidate prompt and rationale. | A correction produces a reviewable proposal evaluated against baseline and held-out cases; no automatic production promotion. | LAB-01/02/03; keep hidden cases inaccessible to the proposer. |
| LAB-05 | **Model comparison:** evaluate eligible models on the same coaching tasks. | A report compares quality, latency, and available cost with model/settings recorded and missing usage explicit. | LAB-01 and API model policy; choose models at implementation time. |
| LAB-06 | **Portfolio walkthrough:** tell one complete synthetic coaching story in a short guided demo. | A reviewer can reproduce the flow, inspect architecture/tests, and see an honest limitation or rejected improvement. | Existing synthetic environment; add workflow/Lab scenes as they land. |
| LAB-07 | **Accessibility and mobile usability pass:** improve one complete capture-to-review flow. | Keyboard navigation, focus, labels, loading/errors, small screens, and interrupted network behavior work in that flow. | Existing Web first; mobile distribution remains gated by TRUST-06. |

There are **46 selectable items**. Useful combinations for a future prioritization conversation are:

- **A visible product improvement:** PRODUCT-01 + PRODUCT-06, then PRODUCT-03.
- **Configure and diagnose integrations:** CONFIG-01 + OPS-01 first, then OPS-02 and OPS-03/05.
- **Safer unattended work:** TRUST-01 + FLOW-02, then FLOW-06.
- **A distinctive portfolio demo:** LAB-01 + LAB-02 + LAB-06, then the remaining Learning Lab slices.
- **Recovery confidence:** DATA-01 + DATA-02/03 + FLOW-05.

These are possible selections, not assigned priorities. Parallel proposals with overlapping persistence, authorization, or telemetry work should share one design rather than create competing implementations.

## Deferred and cleanup follow-ups

- Another Railway environment: revisit for platform-specific release testing or increased collaborators.
- Broad multi-agent orchestration: add only where evaluations support it.
- Versioned database migrations and startup ownership: needed before larger schema changes and embedding migrations.
- Split `PersonalAgentEndpoints` and extract consumer persistence as those capabilities change.
- Audit old deployment/bootstrap scripts against database configuration. They are operational tools, not proven dead code; do not delete them solely because local searches show no callers.

## Completed in this review

- Decision register, retrospective RBAC/configuration records, gateway direction, roadmap, architecture, and operational docs.
- Retired test-event contracts, consumers, chat tool, unused model agent, and demo-only harness tests.
- Removed four always-passing placeholder tests, unused MAUI robot image, and two obsolete project-level Dockerfiles.
- Excluded private seed files, environment files, credentials, and database dumps from root Docker build context; ignored snapshot artifacts in Git.
- Replaced the playground README with a product overview and explicit current limitations.
- Migrated generic .NET skills and OpenCode tooling to user configuration with verified copies and a preserved backup; retained repository conventions in AGENTS.md.
- Removed four unreferenced API DTOs and unused legacy Web authentication options; consolidated service local-start instructions into the shared runbook.

## Implementation progress: snapshot and restore slice

- Added `export-database-snapshot.ps1` and `restore-database-snapshot.ps1` with a shared module and development state-reset SQL. See [usage](../runbooks/snapshot-commands.md).
- Full archives include checksum/version manifests; restore can only create a new local Docker target. Recovery mode has no network; development mode binds loopback and clears runtime credentials/action state while retaining private domain data.
- Tests use the real encryption seed and synthetic data. The CI job is configured to run the snapshot suite; 37 checks passed locally on Windows Docker Desktop on 2026-09-07. Linux CI also passes in PR #34 after fixing readiness to wait for the final PostgreSQL TCP listener.
- Still outstanding in item 1: manifests for scheduled S3 backups, retention/monitoring, real production-archive rehearsal, and full API/Web/Worker recovery checks. The synthetic seed and local startup path are now implemented below. A successful synthetic startup or database restore alone does not complete item 1.
- Added MIT license at the maintainer's request; third-party notices still need review before publication.
- Scheduled tasks now request the API default model; two contract tests exercise different API-selected defaults without Worker configuration changes. The API now refreshes provider inventory at runtime, filtered through reviewed chat/tool policy. See [catalog behavior](../runbooks/runtime-chat-models.md); the transcription adapter migration is now implemented.
## Implementation progress: runtime model catalog

- API-local discovery and catalog interfaces separate provider inventory from application selection policy. OpenAI is the current adapter; clients keep the existing contract.
- Cached refresh, timeout/backoff, fallback, authoritative empty inventories, and a single default are covered by tests. Journal parsing resolves the default per job; saved conversations retain their chosen model.
- Reviewed policy still loads at startup; this is runtime provider availability, not automatic adoption of unreviewed models or live database configuration reload.

## Implementation progress: shared tool registry

- Local tool metadata and factories now share one registration; MCP functions enter the same catalog and binding path. Stable keys and role defaults are preserved.
- Authorization and availability are checked at binding and again at execution. Context-bound factories omit profile/actor/role from the model's argument schema. Descriptive side-effect metadata does not imply automatic approval enforcement.
- Eight registry tests cover all local bindings, catalog/function parity, Coach restrictions, separate subjects, forged profile arguments, revoked permissions, unavailable MCP integrations, duplicate names/keys, and invalid context. See [adding tools](../runbooks/adding-agent-tools.md).
- This completes slice 5 of item 2. Transcription provider migration and persisted jobs subsequently landed. Transactional domain completion subsequently landed (see below); embedding-space ownership and scheduled actor propagation remain outstanding.
## Implementation progress: transcription gateway

- Moved AssemblyAI adapter, configuration, HTTP client and provider mapping into API. Worker uses upload-reference request/response contracts and retains speaker attribution and domain processing.
- API persists the provider job ID, absolute deadline and terminal result; concurrent submission and replay share one job. Ambiguous submissions require review instead of automatic resubmission.
- Added provider mapping, Worker contract and PostgreSQL restart/replay, ownership, concurrency and timeout tests. See [rollout and operational limits](../runbooks/transcription-gateway.md).
- Still outstanding: automated ambiguous-job reconciliation, OS process-kill recovery evidence, and embedding-space migration. Transactional domain completion/outbox and host-restart transport tests subsequently landed (see below). Do not mark the broader durable-workflow item complete based on this adapter move.
- Initial local validation: API and Worker builds passed without warnings; 63 API non-database tests, 9 Worker non-database tests and 13 Contracts tests passed. Five new PostgreSQL job tests were initially blocked by Docker availability. On 2026-09-10, the synthetic-environment work ran the full API suite successfully, including all five PostgreSQL job tests. This supplies service-level recovery evidence, not process-kill or end-to-end outbox guarantees.
- Added a transactional AssemblyAI configuration migration using the existing crypto implementation, with conflict detection and optional source retention. The Api-scoped production key was populated and verified against the retained Worker copy on 2026-09-10; services were not redeployed. Six migration-plan tests cover re-encryption, wrong keys, conflicts, flags and replay.

## Implementation progress: synthetic local environment

- Added `compose.synthetic.yml` with an independent PostgreSQL volume, encrypted synthetic configuration, deterministic API providers, and a Development-only cookie sign-in for two owners and an assigned coach. Seeds conversations, vector memory, and speaker review. No private values file or provider account is required.
- API and Web expose startup readiness probes. Compose waits for API initialization instead of a fixed Worker sleep; Firebase is optional in manual Compose. See [demo commands and limitations](../runbooks/synthetic-demo.md) and [decision 0008](../decisions/0008-synthetic-local-environment.md).
- Full startup exposed and fixed the scoped MassTransit request client captured by singleton transcription service and API authorization denials returning HTTP 500 instead of 403.
- Local validation on 2026-09-10: 23 Contracts, 71 API (including the five transcription PostgreSQL tests), 12 Web, and 12 Worker tests passed. The synthetic HTTP smoke suite passed 22 checks across encrypted startup, actor/subject checks, pgvector recall, bus transcription to speaker review, and Web cookie/antiforgery flows. CI is configured to run this stack and smoke suite; its remote result is not yet recorded.
- Still outstanding: real-archive recovery evidence, scheduled backup manifests/monitoring, and model-quality evaluation. Synthetic token hashes and fixed responses do not measure retrieval or generation quality.


## Implementation progress: transactional transcription completion

- Added an Npgsql outbox in the domain schema and a retrying Worker dispatcher. Transcription, speaker-review continuation, and final processing commit domain changes with outgoing messages; processing commands target their queue directly.
- Upload locks and state/subject/session checks make concurrent redelivery harmless after completion. Confirmed transcription failures also use the outbox. SQL/transport failures and cancellation remain retryable rather than overwriting committed state with Failed.
- Seven new recovery tests cover partial-write rollback, cached provider recovery, lost acknowledgement, stable message IDs, concurrent replay, final-write identity, cancellation, terminal failure, speaker review, and PostgreSQL MassTransit host restart with queued delivery and hosted outbox draining.
- Local validation on 2026-09-10: 19 Worker, 71 API, and 23 Contracts tests passed. Tests use synthetic providers and disposable PostgreSQL; OS process-kill and live-provider evidence remain outstanding. This completes the domain outbox slice, not the broader durable-workflow roadmap. See [decision 0009](../decisions/0009-transcription-outbox.md) and [operations](../runbooks/transcription-gateway.md).
