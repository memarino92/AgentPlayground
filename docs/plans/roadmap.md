# Roadmap

Recorded 2026-09-07; reconciled 2026-09-13 against `b7157f7`. The numbered themes retain the original suggested sequence; the [selection table](#selectable-backlog) records delivered slices and remaining acceptance work. The catalog is unprioritized. Dated validation counts are historical evidence, not results from this documentation review. Estimates remain deferred until slices expose their integration work.

## .NET 11 RC1 adoption — server validated; Android build blocked

Selected 2026-09-13 at the maintainer's request. Projects target .NET 11, with the SDK pinned in `global.json`, central RC1 framework/MAUI versions, matching container tags and CI SDK selection. Blazor authentication-state and interop analyzer fixes preserve warnings-as-errors. Server validation: 327 tests, three Linux Release images, 30 application/audio smoke checks, 13 integration/restart checks and 14 notification checks. Browser sign-in/chat/playback and coach access were verified with synthetic data. Android workload completion/build verification is blocked by local disk space; production deployment and device identity/release acceptance are separate. See [decision 0024](../decisions/0024-dotnet-11-rc1.md).

## Navigation and recording usability — delivered 2026-09-13

Maintainer-requested changes now persist job/chat selections and filters in query parameters, keep the chat list open on selection, start local drafts automatically, remember models, and unify Settings/integrations and owner/coach recording views. The menu dismisses on outside clicks; Markdown block spacing is corrected. Recordings use filename-derived dates, inline audio and the same compact, playback-following transcript as the evidence drawer. Following can be disabled. This extends PRODUCT-01 presentation; its backup/recovery acceptance remains outstanding.

Validation: 53 Web and 218 API tests pass. Synthetic browser checks cover single-Back job/filter restoration from the result conversation, chat Back/list retention, menu dismissal, recording Back, timestamp-to-active-row synchronization, and coach playback without owner controls. See [decision 0023](../decisions/0023-query-navigation-and-chat-preferences.md) and the [navigation runbook](../runbooks/navigation.md).

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

**Decision follow-up:** the boundary review below adds `DEBT-02` to reassess the proposed orchestration owner before implementing this theme or `FLOW-01`. Agent Framework ownership of the entire coaching lifecycle remains a proposal, not an accepted implementation choice.

**Observed:** conversational tool calling, retrieval, scheduling, and manual speaker review exist, but full workflow/session state is not durable. `TranscribeCoachCallConsumer` catches failures and records them, so exceptions do not automatically participate in the normal retry pipeline after that catch.

**Deliver:** migrate the coach pipeline through explicit states with persisted checkpoints and human review. Add restart, redelivery, rejection, timeout, and provider-failure cases. Retain MassTransit for reliable delivery; let the workflow define progress and pause/resume behavior. Verify C# package compatibility first.

**Done when:** an in-flight job survives a process restart and a delayed human decision without duplicate provider submission or final writes. Show its trace and evidence-linked result in a synthetic demo. See [0005](../decisions/0005-durable-garden-workflows.md).

## 5. Portfolio presentation and measured improvement

**Deliver:** a short synthetic-data walkthrough: upload a coach note, attribute speakers, retrieve a cited cue, schedule a follow-up, then show a resumed workflow. Add screenshots, a two-minute demo, an architecture explanation, and a recovery rehearsal result. Publish only synthetic examples, never production journal/transcript excerpts.

Introduce a versioned evaluation set for retrieval relevance, grounded answers, extraction accuracy, authorization, tool errors, latency, and cost. Capture user corrections. Let a review workflow propose prompt/memory/tool-policy improvements; compare against the baseline, approve, deploy, and roll back if results worsen.

2026-09-11 partial LAB-01/MEMORY-02 delivery: a synthetic PostgreSQL baseline reproduces missing untagged exercise cues, verifies timestamped citations and subject isolation, and exercises exact filename follow-ups. Search now treats exercise tags as relevance hints with transcript lexical recovery; newly processed yoke chunks receive a tag. Fourteen retrieval/chat cases and the second-utterance processing regression pass locally, including vector ranking without tags and new chats across service recreation against unchanged legacy chunks. This is deterministic retrieval coverage, not live model quality, full hybrid ranking comparison, or completed LAB-01. See [procedure and remaining evaluation scope](../runbooks/coach-retrieval-evaluation.md) and [decision 0015](../decisions/0015-coach-retrieval-hints.md).

2026-09-12 recency iteration: latest-call scope, filename-derived recording dates, a bounded default recency preference, and utterance-level evidence are implemented with synthetic PostgreSQL regressions. A production snapshot was restored into an isolated local development database and the indexing/attribution failure mechanisms were inspected privately. Authorized live-model replay with the local production copy now retrieves the newest coaching correction and distinguishes a neighboring exercise cue; this does not complete LAB-01, MEMORY-02, semantic exercise classification, editable call dates, or full application recovery. See [decision 0017](../decisions/0017-coach-recording-recency.md).

2026-09-12 LAB-01/LAB-05 first formal comparison: repository-owned opt-in runner, frozen private cases, three repeats per model, per-check grading, raw traces, source/dataset hashes, usage/latency, and JUnit evidence are implemented. 108 live trials exposed and verified fixes for Luna tool compatibility and escaped evidence links; corrected rubric v2 retains the original grades and rescored all outputs consistently. Post-fix Luna scored 17/18 and GPT-5.4 Mini 16/18, with remaining citation failures recorded. Scorer/application regression suite: 172 passed. This remains a small tuning set; held-out coverage, broader groundedness, and automatic promotion remain incomplete. See [formal comparison](../runbooks/coach-retrieval-evaluation.md#formal-low-cost-model-comparison-2026-09-12).



**Done when:** a reviewer can understand the problem, run a demo without private data, inspect tests/decisions, and see measurable engineering tradeoffs. Complete the [public-release review](../runbooks/public-release.md) before changing visibility.

2026-09-11 model-policy follow-up: production chat choices now read active Shared/Api database policy on each catalog request, with insert-only startup seeding and immediate provider-cache invalidation after edits. Appsettings model lists and the hardcoded empty-policy fallback are removed. Existing policies and saved-session model IDs are preserved. See [decision 0016](../decisions/0016-database-chat-model-policy.md) and [runtime operations](../runbooks/runtime-chat-models.md).

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

## Scheduled-task authorization — scheduled agent jobs implemented

Recorded 2026-09-10. Expand the scheduling authorization follow-up into a bounded delivery item, recommended before Learning Lab implementation.

2026-09-12: TRUST-01 and the conservative scheduled-job slice of DEBT-01 are implemented with a FLOW-02 dashboard and pending cancellation. API persists trusted actor/subject/source conversation, checks current database sign-in policy, assignments and tool permissions, owns execution by TaskId, and commits completion/notification intent through the domain outbox. Interrupted runs recover saved responses or become NeedsReview without blind replay. Legacy deliveries are blocked. See [decision 0021](../decisions/0021-scheduled-jobs.md) and [operations](../runbooks/scheduled-jobs.md). General workflow resume, external-tool operation journals, other job types and recurrence remain open.

2026-09-12 extension: scheduled notifications now share job identity, current authorization, scheduler attribution, pending cancellation, attempts and the dashboard. Notification outcomes distinguish provider acceptance from unavailable or uncertain delivery. Old transport-only reminders remain outside the dashboard. Validation: 217 API tests, 45 Web tests, 14 synthetic notification checks and desktop/mobile browser checks passed. See [decision 0022](../decisions/0022-scheduled-notifications.md).

Validation: 204 API, 44 Web, 30 Worker and 23 Contracts tests passed locally (301 total); the synthetic scheduled-job smoke suite passed 15 checks, including Worker restart and permission revocation. Edge verified dashboard cancellation, read-only result navigation and a narrow viewport without horizontal overflow. No production deployment is claimed.

**Observed before implementation:** `PersonalAgent.Worker/Services/AgentTaskExecutionService.cs` sent scheduled execution requests through `SendAsOwnerAsync`, with the role fixed to `Owner`. That path is now removed.

**Deliver:** persist the trusted originating actor, subject, and authorization context when scheduling work; propagate that context through durable messages and execution. Resolve current permissions server-side when the task runs, including retries, so stored roles cannot preserve revoked access. Remove unconditional Owner execution. Define a migration path for existing schedules with missing actor context; do not silently grant them Owner authority. Record the authorization decision before implementation, extending [0002](../decisions/0002-actor-and-tool-authorization.md).

**Done when:** tests demonstrate authorized execution, Coach restrictions, cross-user rejection, permission revocation between scheduling and execution, forged context rejection, and safe handling of legacy schedules. Denied runs have an inspectable outcome and perform no unauthorized tool action; redelivery cannot bypass the checks.

## Sentry and OpenTelemetry/OpenInference observability — service instrumentation implemented

Recorded 2026-09-10 at the maintainer's request. Make application failures and agent execution inspectable across Web, API, Worker, and durable messaging, with useful evidence for the Learning Lab.

**Deliver in slices:**

1. Add Sentry error reporting with service, environment, and release context; correlate failures with the affected request or workflow and define actionable alerts.
2. Add OpenTelemetry tracing, metrics, and log correlation across HTTP, MassTransit delivery, background execution, and provider calls. Preserve correlation through retries, outbox dispatch, and resumed work.
3. Evaluate and adopt OpenInference conventions for agent/model/tool/retrieval spans where compatible with the pinned .NET and Agent Framework packages. Record model/settings, duration, token usage and cost when available, tool outcomes, and prompt/evaluation version identifiers when those features exist. Distinguish unavailable usage from zero and estimated cost from measured usage.
4. Verify Sentry/OpenTelemetry interoperability and OpenInference attribute support before selecting SDKs, exporters, or a trace backend. Record the integration decision, sampling/retention choices, and ownership of instrumentation to avoid duplicate spans and error reports.
5. Document local synthetic validation, deployment configuration, and a troubleshooting walkthrough. Keep secrets and private journal/transcript content out of exported telemetry by default; verify redaction with synthetic fixtures. Telemetry export failures must not prevent application work.

**Done when:** a synthetic coaching request can be followed across service and message boundaries; an injected failure produces a correlated Sentry issue with release context; a retry or resumed job remains traceable; agent spans expose useful timing and available usage data. Verify content redaction, exporter-outage behavior, and absence of duplicate instrumentation. Provide an operational view of error rates, latency, queue/outbox delays, and provider failures, with a documented path from an alert to the relevant trace.

## In-app integration settings — Sentry first slice implemented

Recorded 2026-09-10. The maintainer selected Sentry and in-app settings for implementation. Encrypted revision storage, the administration form, save/apply/reload, service acknowledgements, and metadata-only Sentry error reporting now exist. See [decision 0010](../decisions/0010-runtime-integration-settings.md) and [setup](../runbooks/integration-settings.md). Maintainer-confirmed deployed Sentry receipt, live API provider credentials, and database-configured OpenTelemetry have since shipped. History/revert UI, broader typed reload, deployed trace validation and alerts remain open.

**Deliver:** a registered, typed settings form with encrypted secret storage, deployment-administrator authorization, immediate field validation, server validation, and explicit save/apply actions. Show configured secrets without revealing them, effective overrides, saved versus running revisions, validation results, and per-service application status. Bootstrap database access and the encryption key remain external. New integration adapters define their supported fields and reload policy; arbitrary keys do not add capabilities.

**First vertical slice:** configure Sentry enabled state, DSN, environment, and supported sampling settings; send a synthetic test event; then show which services applied the revision. Verify SDK lifecycle behavior before promising live reload. Support validated live updates or controlled reinitialization where safe and clearly indicate restart-required settings. Keep the last working configuration on validation/application failure and reconcile services that were offline.

**Done when:** an authorized administrator can configure and test Sentry entirely through the application settings flow after normal deployment bootstrap; invalid or conflicting edits cannot silently replace working settings; secrets never return in read responses; overrides and partial application are visible; supported reload/disable/re-enable paths are tested without duplicate reporting. The selected `CONFIG-01`/`OPS-01` Sentry slice is delivered; broader provider migration remains separate.

## Selectable backlog

Recorded 2026-09-10. Entries are **unprioritized proposals unless selected below**, including remaining slices of existing roadmap themes. The catalog descriptions are acceptance targets, not implementation claims. This catalog includes the Learning Lab, scheduled authorization, and observability plans above; their detailed descriptions remain authoritative. Implementation evidence remains in the progress sections below.

Use the stable IDs to pick work, for example `PRODUCT-01` or `OPS-02`. Categories do not imply delivery order. Each row describes a bounded first slice and an observable acceptance outcome, rather than the entire possible feature. Dependencies describe prerequisites for that slice; investigation can start earlier. Before selecting work, verify the current implementation and narrow scope. Record significant architecture choices using the decision template before implementation.

When an item is selected, add its ID to the selection table and define the exact scope, validation, and exclusions. Update status and evidence here when it lands; do not maintain a separate conflicting status list. Suggested statuses are Proposed, Selected, In progress, Blocked (with reason), Done (with evidence), and Deferred.

| Selected ID | Status | Scope and completion evidence |
| --- | --- | --- |
| CONFIG-01 + OPS-01 | Done — selected Sentry/settings slice | Encrypted revisions, administrator save/apply/reload, service acknowledgements and reporting; maintainer confirmed deployed test-event receipt. Existing-row editor and live API credentials also delivered. History/revert, new rows and broader typed reload remain follow-ups. Decisions 0010–0012. |
| PRODUCT-01 + DATA-04 | In progress — audio and presentation delivered | Retained audio, authorized evidence/playback, terminal audio deletion, completed-call attachment and optional playback following. Recording-inclusive recovery, archive deletion reconciliation and broader retention remain open. Decisions 0013 and 0023. |
| TRUST-01 | Done — agent and notification jobs | Trusted attribution, current authorization, revocation and legacy handling covered by recorded regression and synthetic checks. Decisions 0021–0022. |
| DEBT-01 + FLOW-02 + FLOW-04 | Done — bounded scheduled-job slice | Durable identity, attempts, dashboard, pending cancellation, saved-result recovery and NeedsReview for uncertain execution. General resume, other job types, external-effect journals and human-review expiry remain open. Decisions 0021–0022. |
| OPS-02 + OPS-03 | In progress — service implementation delivered | Instrumentation, OpenInference, Sentry correlation, outbox trace context, redaction and live database export settings. Complete deployed cross-service trace evidence, backend validation and OPS-05 alerts remain open. Decisions 0019–0020. |
| LAB-01 + LAB-05 + MEMORY-02 | In progress — first evaluation slices delivered | Synthetic retrieval regressions and repeatable live-model comparison with usage, latency and JUnit evidence. Held-out quality coverage and full vector/lexical/hybrid comparison remain open. Decisions 0015, 0017–0018. |
| DATA-02 + FLOW-05 | In progress — partial recovery evidence | Snapshot tooling, synthetic startup, local production-copy retrieval checks and Worker restart smoke tests exist. Full production-archive application recovery, measured backup age/recovery time and broader process-kill boundaries remain open. |

### Everyday product value

| ID | Idea and first slice | Done when | Dependencies / scope notes |
| --- | --- | --- | --- |
| PRODUCT-01 | **Evidence drawer with original call audio:** open a cited coaching answer beside the exact transcript excerpt, speaker, and timestamp, and listen from that point in the retained recording. | Citations open the correct authorized source and audio position; podcast controls support seeking, speed, and ±15 seconds. Missing or deleted evidence has an explicit state. | Detailed plan below; retain recordings after processing; coordinate DATA-01/04. Optional follow-along is delivered; recording-inclusive recovery remains open. |
| PRODUCT-02 | **Coaching timeline:** browse cues and corrections by date, coach, and topic. | A synthetic sequence shows how advice changed and links each entry to its source. | Use existing source records first; inferred relationships require review. |
| PRODUCT-03 | **Weekly reflection draft:** summarize wins, recurring difficulties, and open questions from a chosen week. | The user can edit and save a draft with evidence links; unsupported claims are omitted or clearly flagged. | On-demand first; automated delivery depends on TRUST-01. |
| PRODUCT-04 | **Follow-up inbox:** collect proposed tasks and questions from coaching notes. | The user can accept, edit, dismiss, and trace each suggestion to its source without creating duplicate tasks. | Build on existing task and approval records; TRUST-01 before scheduled execution. |
| PRODUCT-05 | **Goal progress journal:** attach notes and coaching cues to a user-defined goal. | A user can record a goal, link evidence, and review progress without invented completion claims. | Manual links first; automatic suggestions later. |
| PRODUCT-06 | **Session preparation brief:** prepare a short agenda before a coaching call. | The brief contains recent corrections, unresolved questions, and relevant sources that the user can edit. | Existing records; start on demand. |
| PRODUCT-07 | **Unified search:** search journals, transcripts, and conversations with date/source filters. | Results enforce subject access and open the right source; empty states and pagination work. | MEMORY-02 can improve ranking later. |
| PRODUCT-08 | **Export a useful artifact:** turn a selected coaching recap into Markdown with source references. | Preview and export contain only selected, authorized content and preserve useful citations. | Start with Markdown; other formats are separate slices. |

### Evidence drawer and retained call audio — first playback slice implemented

Recorded 2026-09-11 at the maintainer's request; expands `PRODUCT-01`. Original call recordings remain in the PostgreSQL upload row after successful processing. Subject-authorized evidence and range playback, Owner-only terminal audio deletion, retrieval citation links, and the transcript/player drawer are implemented. The storage/lifecycle proposal is recorded in [0013](../decisions/0013-retained-call-audio.md); see operational limits in the [transcription runbook](../runbooks/transcription-gateway.md). Optional follow-along and shared inline playback shipped on 2026-09-13. Recording-inclusive backup recovery validation remains outstanding.

**Deliver in slices:**

1. Retain the original recording with a durable link to its call, transcript, and subject instead of deleting it on successful processing. Preserve completion/outbox atomicity and replay safety from [0009](../decisions/0009-transcription-outbox.md). Before implementation, record the storage and lifecycle decision, including database versus object storage, capacity/cost, backup and restore, and authorized deletion under `DATA-04`.
2. Preserve source call identity and transcript start/end offsets through chunking, retrieval, and citations. Open the evidence drawer with the retrieved excerpt, speaker, timestamp, and surrounding transcript; offer playback of the original call starting at the cited timestamp. Keep offsets tied to the original audio timeline so transcript corrections do not silently shift playback targets.
3. Add podcast-style play/pause, a seekable progress bar, elapsed/total time, playback-speed selection, and skip backward/forward 15 seconds, clamped to the recording bounds. Let the user keep listening beyond the excerpt for context. Include accessible labels and keyboard controls, and clear loading/playback-error states.
4. Delivered 2026-09-13 — optional follow-along: highlight the active timestamped utterance and scroll the transcript as audio plays, with a toggle to pause automatic scrolling while reading elsewhere. Clicking a timestamped utterance seeks the audio; seeking or changing speed keeps highlighting aligned. Use utterance-level timing initially; finer highlighting depends on available alignment data.

**Done when:** a synthetic cited answer opens the correct excerpt and retained original audio at its source timestamp after processing and a service restart. Playback, seeking, speed changes, and ±15-second skips work across recording boundaries; follow-along stays synchronized and can be disabled. Transcript and audio requests enforce the same server-side subject/access policy, including playback range requests. Older calls whose audio was already deleted remain readable with an explicit audio-unavailable state; missing timing never produces a fabricated seek target. Authorized deletion removes active audio access and leaves clear unavailable evidence states, with backup retention limits documented. Recovery validation includes retained recordings and their citation links.

**Validation (2026-09-11):** all 108 API, 25 Web and 25 Worker tests passed. Coverage includes retention/recovery, range playback, subject isolation, assignment revocation, terminal-only deletion, readable transcripts after deletion/restart, citation opening, missing media/timing and deletion confirmation. The synthetic smoke suite passed 30 checks through real processing and authenticated Web media requests; CI now runs it in place of the original 22-check suite. Browser checks verified the 4-second source seek, play/pause, seek bar, 1.5× speed, timestamp clicks and 15-second skips clamped to both ends of a 30-second fixture. A recording-inclusive backup/restore rehearsal and archive-deletion reconciliation remain outstanding; PRODUCT-01's full recovery acceptance is not complete.

**Manual attachment follow-up (2026-09-11):** the transcript page now offers Owner-only **Upload audio** / **Replace audio** for the selected completed call; these controls are outside the evidence drawer. File selection attaches the recording immediately without hash/transcript/duration verification or reprocessing; source identity and transcript timing stay unchanged. All 116 API and 30 Web tests passed, including transcript-page placement and subject-binding regressions. Added coverage includes missing/replaced audio across host replacement, unchanged ingestion hash and no outgoing processing messages, subject/state restrictions, byte limits and file-picker success/error behavior. Browser testing replaced a 30-second synthetic recording with a 20-second recording and retained the existing four-second seek target.

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
| CONFIG-01 | **In-app integration settings:** enter, validate, save, and apply typed integration configuration with encrypted secrets. | Sentry setup and a synthetic test event work through the UI; saved/applied revisions, overrides, failure, and restart requirements are accurate per service. | Accepted decision 0010; dedicated integration migration implemented; broader DATA-01 remains open. |
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
| DATA-04 | **Retention and deletion:** define lifecycle policies for source audio, transcripts, derived memory, and telemetry; retain original call audio after successful processing for PRODUCT-01 rather than deleting it on completion. | A preview identifies affected records; authorized deletion removes active audio access and derived retrieval data, with explicit unavailable citation states; backup retention limits are documented. | DATA-01 and PRODUCT-01; storage/retention decision before implementation, including capacity and recovery implications. |
| DATA-05 | **Release confidence:** extend CI with reproducible service smoke checks and an Android release build. | A clean build exercises the synthetic server flow and produces a mobile artifact without production credentials. | Extend existing CI rather than duplicating it; record remote results. |
| DATA-06 | **Focused maintainability work:** split endpoint groups or consumer persistence while implementing a related feature. | The selected capability has clear ownership and existing route/auth/replay behavior remains covered. | Couple to a selected feature; no repository-wide rename or speculative rewrite. |

### Technical debt

Recorded 2026-09-10 from the MassTransit / Microsoft Agent Framework boundary review. Except for the selected scheduled-job slice recorded below, these remain **proposed, not implemented**. Keep MassTransit for delivery and scheduling, Agent Framework for agent execution, and application code responsible for business state, authorization, and completion semantics. Existing related backlog items remain authoritative for their feature scope; the entries below add boundary and recovery acceptance criteria rather than separate competing implementations.

Selected on 2026-09-12: `DEBT-01` with `TRUST-01` and a scheduled-agent-task `FLOW-02` dashboard. This slice is implemented using conservative recovery; see decision 0021. The remaining boundary items, including the workflow ownership decision in `DEBT-02`, remain proposals.

| ID | Idea and first slice | Done when | Dependencies / scope notes |
| --- | --- | --- | --- |
| DEBT-01 | **Durable scheduled-task execution:** persist a run keyed by `TaskId`, pass that identity into API execution, and retain its session/run reference, status, and result. Distinguish retryable failures from terminal outcomes instead of converting every exception into a normally completed consumer call. | Duplicate delivery and restart retrieve or resume the same operation; tested failure windows do not repeat committed tool effects; terminal outcome and completion notification are recorded atomically through an outbox. Cancellation and transient failures follow an explicit retry policy. | Implement with TRUST-01: persist originating actor/subject, revalidate current permissions, and remove unconditional Owner execution. An idempotent task endpoint alone does not make every external tool effect idempotent; define per-operation recovery and ambiguous-result handling. |
| DEBT-02 | **One durable workflow owner:** revisit proposed decision 0005 before FLOW-01. Compare an application process manager / MassTransit saga for the outer coaching lifecycle with an Agent Framework workflow; consider bounded agent workflows for extraction, grounding, and revision steps. | A reviewed decision names the authority for progress, deadlines, human waits, cancellation, and completion; distinguishes transport retry from workflow replay; and defines restart/duplicate-message acceptance cases without two engines independently completing the same job. | Preserve the existing coaching outbox and provider-job safeguards. Verify any selected APIs against pinned packages. A saga or workflow migration is not approved merely by adding this item. |
| DEBT-03 | **Durable approval continuation:** connect approval records to a specific pending operation and atomically persist the decision plus its continuation message. Current approval events do not by themselves resume an agent run. | Authorized approval resumes the bound operation once; stale, altered, cross-subject, rejected, expired, and duplicate decisions cannot execute it; a restart between decision and delivery does not lose the continuation. | Extend TRUST-02 and decision 0002; coordinate with DEBT-02 and reuse the transactional outbox approach. |
| DEBT-04 | **Chat execution and session recovery:** make the distinction between saved transcript and resumable execution explicit. Evaluate persisting framework session/tool state or an application operation journal, with versioned state and durable concurrency control where needed. | Recovery tests cover a tool effect occurring before the final response is saved, with no blind repeat of an ambiguous effect; prior tool outcomes remain traceable; the chosen policy handles concurrent turns across supported API instances. | Current AgentChatService creates a fresh framework session per turn, stores user/assistant text, and uses process-local session locks. Verify pinned session APIs; keep authorization checks on resumed work. Coordinate side-effect identities with DEBT-01/03. |
| DEBT-05 | **Domain handlers independent of transport:** extract coaching state transitions and persistence from consumers into focused application handlers/stores; wrap the embedding request client behind a capability interface. Separate application storage configuration from SqlTransportOptions. | Consumers adapt messages into handler calls; business processing can be exercised without a bus context; existing transaction, ownership, and replay guarantees remain covered. Domain storage no longer depends on transport configuration types. | Refines DATA-06; start with one coaching capability. Keep the same PostgreSQL deployment and avoid a repository-wide restructuring. |
| DEBT-06 | **Bounded background steps and short transactions:** replace the long-lived Worker transcription polling loop with persisted progress and scheduled checks or completion messages. Move embedding calls outside the final coaching row-lock transaction using a durable claim and guarded commit. | Waiting releases consumer capacity; restart resumes the known provider job; concurrent workers cannot commit conflicting results; external calls do not hold the final database row lock; stale claims and cancellation have tested outcomes. | DEBT-02 determines progress ownership. Preserve decision 0009's atomic result/outbox commit and conservative handling of ambiguous provider submissions; coordinate FLOW-03/04/05. |
| DEBT-07 | **Complete embedding ownership:** implement MEMORY-05 with API-owned embedding-space identity, dimensions, compatibility policy, and re-embedding coordination; remove Worker's fixed vector(1536) assumption. | Storage and retrieval reject incompatible spaces even when dimensions match; a resumable migration validates new vectors before activation; the provider-swap contract is verified across API and Worker. | This is the technical-debt acceptance detail for MEMORY-05 and decision 0004, not another migration project. Coordinate schema ownership with DATA-01. |
| DEBT-08 | **Shared infrastructure and Web dependencies:** separate neutral wire contracts from PostgreSQL configuration, transport bootstrap, and outbox implementation. Web now requires its bus for live coaching status subscriptions (decision 0014). | Contract consumers do not acquire database/transport implementation dependencies merely to use DTOs; Web authentication, API calls, Data Protection persistence, startup, status fan-out and reconnect reconciliation remain covered. | Refines DATA-06; preserve required Web database access and deployed configuration. Preserve Web status subscriptions unless an accepted replacement provides equivalent behavior; do not introduce another service or broker. |

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

There are **54 catalog items**, including delivered and partially delivered slices above. Useful remaining combinations for a future prioritization conversation are:

- **A visible product improvement:** PRODUCT-06, then PRODUCT-03, using delivered evidence/playback views.
- **Diagnose integrations in operation:** finish OPS-02/03 deployment evidence, then OPS-05 dashboards and alerts.
- **Safer unattended work:** FLOW-06 on the delivered authorization/dashboard foundation; DEBT-02 before general workflow resume.
- **A distinctive portfolio demo:** extend LAB-01 held-out coverage, then LAB-02 + LAB-06 using the existing runner.
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

- Follow-up on 2026-09-11: upload, transcription start, speaker review/processing, review continuation, completion and terminal transcription failure now enqueue metadata-only status events with their database changes. Each Web instance receives its own SQL subscription; upload/admin/transcript pages refresh through authorized APIs and reconcile every 30 seconds. Draft speaker choices survive refresh and the first apply updates roles immediately. Local tests cover transactional replay, SQL fan-out to two instances, component refresh, disposal, subject filtering and speaker-review continuation. This completes live state refresh, not saga orchestration, granular chunk progress, or retry/reprocess controls. See [decision 0014](../decisions/0014-live-coach-processing.md).
- Validation for live refresh: 116 API, 28 Worker, 35 Web and 23 Contracts tests passed locally. PostgreSQL tests use disposable databases and synthetic providers; no deployment or live-provider verification is claimed.

- Added an Npgsql outbox in the domain schema and a retrying Worker dispatcher. Transcription, speaker-review continuation, and final processing commit domain changes with outgoing messages; processing commands target their queue directly.
- Upload locks and state/subject/session checks make concurrent redelivery harmless after completion. Confirmed transcription failures also use the outbox. SQL/transport failures and cancellation remain retryable rather than overwriting committed state with Failed.
- Seven new recovery tests cover partial-write rollback, cached provider recovery, lost acknowledgement, stable message IDs, concurrent replay, final-write identity, cancellation, terminal failure, speaker review, and PostgreSQL MassTransit host restart with queued delivery and hosted outbox draining.
- Local validation on 2026-09-10: 19 Worker, 71 API, and 23 Contracts tests passed. Tests use synthetic providers and disposable PostgreSQL; OS process-kill and live-provider evidence remain outstanding. This completes the domain outbox slice, not the broader durable-workflow roadmap. See [decision 0009](../decisions/0009-transcription-outbox.md) and [operations](../runbooks/transcription-gateway.md).

## Implementation progress: runtime integration settings and Sentry

- Follow-up on 2026-09-11: one Settings page contains Sentry and existing database settings. OpenAI and AssemblyAI API keys in Shared/Api scopes now reload for new API requests automatically or through an explicit reload check. Invalid credential syntax preserves the running snapshot; other settings retain restart labels. All 96 API and 20 Web tests pass, including real PostgreSQL reload and fake HTTP credential rotation. Remaining consumers and lifecycle constraints are recorded in [decision 0012](../decisions/0012-live-provider-credentials.md).

- Added `AgentPlayground.Integrations`, a dedicated host-infrastructure library with encrypted immutable configuration revisions, optimistic concurrency, promotion history, an additive versioned schema migration, and per-instance acknowledgement records.
- Added a deployment-administrator settings page with secret keep/replace/clear, field validation, save/apply/reload, synthetic test event, and pending/offline status. API requires the signed actor plus an explicit administrator allowlist. Saved secrets are never included in read responses.
- API, Web, and Worker reconcile the active pointer every 15 seconds and replace privately owned Sentry clients without a host restart. Invalid candidates/overrides retain the last working client. Existing startup-only configuration is unchanged.
- Sentry error events carry redacted text and operational metadata; raw log values and exception messages/stacks are excluded. The SDK test uses an in-memory transport and verifies envelope content and bounded replacement. Maintainer confirmed deployed test-event receipt after merging PR #40.
- Local validation on 2026-09-10: 90 API, 16 Web, and 19 Worker tests passed; the final API suite includes 19 integration-specific tests after migration and exporter-failure refinements. The synthetic application suite passed 22 checks, and the integration suite passed 13 checks including a real Worker process restart and three-service revision reconciliation. Browser testing verified form save/apply behavior. CI now runs the integration smoke suite; its remote result is not recorded.
- Follow-up on 2026-09-11: `/admin/settings` edits all existing database rows, including custom keys and inactive settings. Secrets remain masked, changed rows save atomically, external-write conflicts reject the batch, and every legacy setting is explicitly startup-bound. Eight focused API tests and all 20 Web tests passed; the rebuilt synthetic preview lists the existing settings. See [decision 0011](../decisions/0011-existing-settings-editor.md).
- This delivers the first `CONFIG-01`/`OPS-01` slice and the existing-settings editor. History/revert UI, typed validation and reload for legacy consumers, new-row management, broader schema migration ownership and alerts remain separate work. OpenTelemetry/OpenInference shipped subsequently; see the dated updates below. See [decision 0010](../decisions/0010-runtime-integration-settings.md) and [operations](../runbooks/integration-settings.md).

### Observability implementation — 2026-09-12

OPS-02/03 now have shared API/Web/Worker OpenTelemetry HTTP, Npgsql and MassTransit tracing, runtime/messaging metrics, metadata-only correlated logs, explicit OpenInference AI spans and available chat usage. OPS-01 retains its working live Sentry settings and now includes native trace/span correlation. The custom coaching outbox persists W3C traceparent for resumed delivery and retry. Sensitive trace/log data is removed before export; an OTLP endpoint explicitly enables export. See [decision 0019](../decisions/0019-observability.md) and [setup and coverage](../runbooks/observability.md).

Local validation passed 179 API, 40 Web, 30 Worker and 23 Contracts tests (272 total), including redaction, missing usage, exporter outage, Sentry correlation and durable outbox retry. Deployed collector ingestion, a complete synthetic trace across all running services, alert thresholds/dashboard delivery, and native/mobile/browser instrumentation remain open. This does not mark the full observability acceptance criteria complete.
### Database-first live telemetry settings — 2026-09-12

OpenTelemetry now uses isolated encrypted database revisions and the same administrator save/apply/reload flow as Sentry. Export enablement, per-signal switches, collector URL/protocol, secret headers, sample rate and timeout reconcile live across API/Web/Worker. This supersedes the initial environment-configured export approach above. No new environment variables were introduced; AGENTS.md records the maintainer's database-first preference. Export remains off until configured. Railway logs can remain the normal log viewer; external trace ingestion is optional and no backend has been deployed. See [decision 0020](../decisions/0020-live-database-telemetry-settings.md).
