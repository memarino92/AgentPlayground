# Roadmap

Recorded 2026-09-07. Ordered for a predominantly single-maintainer application. Estimates are deliberately omitted until the first slices expose their integration work.

## 1. Reproducible local data and proven recovery — in progress

**Observed:** `infrastructure/backup/backup.sh` uploads custom-format dumps, but the repository has no restore helper or completed rehearsal. Encrypted config, push tokens, staged audio, and SQL transport share the database. Local Compose/examples lag the RBAC/configuration refactor.

**Deliver:** snapshot and restore helpers with named source/target settings, exit-code checks, checksums, an immutable manifest, an empty local target, and local-target guards. Separate full recovery from development sanitization. Use local configuration and a local encryption key; create fresh transport infrastructure before starting services. Add a synthetic seed for development without production access. Fix Compose and examples against the seeded startup contract.

**Done when:** a fresh checkout can start all server services using documented local setup; a production-format backup is restored into an isolated target; scoped chat, config decryption, vector search, and controlled background work pass; measured recovery time and actual backup age are recorded. See [recovery runbook](../runbooks/database-recovery.md).

## 2. AI gateway and consistent tool interface — runtime catalog implemented

**Observed:** journal parsing and embeddings already cross typed bus contracts to API, but AssemblyAI adapter/options live in Worker, `AgentTaskExecutionService` previously selected `gpt-4o-mini` (now delegates the default to API), and journal schema assumes 1536-dimensional vectors. Tool metadata is duplicated between chat and access services.

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

## Deferred and cleanup follow-ups

- Another Railway environment: revisit for platform-specific release testing or increased collaborators.
- Broad multi-agent orchestration: add only where evaluations support it.
- Versioned database migrations and startup ownership: needed before larger schema changes and embedding migrations.
- Scheduling authorization: carry the originating actor/role through scheduled work rather than unconditionally executing as Owner.
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
- Still outstanding in item 1: manifests for scheduled S3 backups, retention/monitoring, synthetic public demo seed, fresh local configuration/Compose parity, real production-archive rehearsal, and full API/Web/Worker recovery checks. A successful database restore alone does not complete item 1.
- Added MIT license at the maintainer's request; third-party notices still need review before publication.
- Scheduled tasks now request the API default model; two contract tests exercise different API-selected defaults without Worker configuration changes. The API now refreshes provider inventory at runtime, filtered through reviewed chat/tool policy. See [catalog behavior](../runbooks/runtime-chat-models.md); transcription migration and registry consolidation remain pending.
## Implementation progress: runtime model catalog

- API-local discovery and catalog interfaces separate provider inventory from application selection policy. OpenAI is the current adapter; clients keep the existing contract.
- Cached refresh, timeout/backoff, fallback, authoritative empty inventories, and a single default are covered by tests. Journal parsing resolves the default per job; saved conversations retain their chosen model.
- Reviewed policy still loads at startup; this is runtime provider availability, not automatic adoption of unreviewed models or live database configuration reload.
