# Project knowledge

This repository is becoming an **agentic digital garden**: a personal application suite that turns conversations, work journals, and coaching notes into useful, retrievable knowledge and follow-up actions.

## Start here

- [Architecture](architecture.md): current responsibilities and boundaries.
- [Roadmap](plans/roadmap.md): work themes, evidence, and acceptance criteria; includes a [selectable backlog](plans/roadmap.md#selectable-backlog) of unprioritized ideas.
- [Local development](runbooks/local-development.md): current setup and known gaps.
- [Synthetic demo](runbooks/synthetic-demo.md): start API/Web/Worker without private data or provider accounts.
- [Snapshot commands](runbooks/snapshot-commands.md): implemented export and guarded local restore.
- [Adding agent tools](runbooks/adding-agent-tools.md): register a capability with shared metadata, binding, and authorization.
- [Runtime chat models](runbooks/runtime-chat-models.md): API discovery, policy, and fallback behavior.
- [Scheduled jobs](runbooks/scheduled-jobs.md): authorization, dashboard, cancellation and conservative recovery.
- [Coaching retrieval evaluation](runbooks/coach-retrieval-evaluation.md): synthetic regression baseline and live-model evaluation limits.
- [Observability](runbooks/observability.md): service tracing, OpenInference, Sentry correlation and OTLP setup.
- [Integration settings](runbooks/integration-settings.md): in-app Sentry configuration, encrypted revisions, and runtime reload.
- [Existing settings editor](decisions/0011-existing-settings-editor.md): edit database configuration with masked secrets and explicit restart requirements.
- [Live provider credentials](decisions/0012-live-provider-credentials.md): OpenAI/AssemblyAI key rotation for new requests; remaining startup-bound consumers.
- [Database recovery](runbooks/database-recovery.md): full recovery procedure and remaining evidence.
- [Public release](runbooks/public-release.md): publication readiness and cleanup inventory.

## Decision register

| Record | Status | Purpose |
| --- | --- | --- |
| [0001: Database configuration](decisions/0001-database-configuration.md) | Accepted, retrospective | Record the encrypted configuration refactor |
| [0002: Actor and tool authorization](decisions/0002-actor-and-tool-authorization.md) | Accepted, retrospective | Record RBAC and its remaining boundaries |
| [0003: Local parity and recovery](decisions/0003-local-parity-and-recovery.md) | Accepted; first slice implemented | Use local rehearsal before adding another hosted environment |
| [0004: Agent service boundary](decisions/0004-agent-service-boundary.md) | Accepted; catalog, registry and transcription gateway implemented | Centralize provider access and tool registration |
| [0005: Durable garden workflows](decisions/0005-durable-garden-workflows.md) | Proposed | Introduce measurable, resumable agent workflows |
| [0006: Personal assistant tooling](decisions/0006-personal-assistant-tooling.md) | Accepted; migrated | Keep generic skills and tooling in user configuration |
| [0007: MIT license](decisions/0007-mit-license.md) | Accepted | Permit broad reuse under standard MIT terms |
| [0008: Synthetic local environment](decisions/0008-synthetic-local-environment.md) | Accepted | Exercise real storage and delivery with deterministic providers and guarded sample sign-in |
| [0009: Transcription outbox](decisions/0009-transcription-outbox.md) | Accepted; implemented | Commit transcription and processing completion with durable outgoing messages |
| [0010: Runtime integration settings](decisions/0010-runtime-integration-settings.md) | Accepted; Sentry first slice implemented | Enter, validate, and apply integration settings in the app |
| [0013: Retained call audio](decisions/0013-retained-call-audio.md) | Proposed; retention and evidence drawer implemented | Preserve and play original recordings with subject access and active deletion |
| [0014: Live coach processing](decisions/0014-live-coach-processing.md) | Accepted; implemented | Refresh open coach pages from transactional MassTransit status events |
| [0015: Coach retrieval hints](decisions/0015-coach-retrieval-hints.md) | Accepted, retrospective; implemented | Recover untagged exercise cues and scope follow-ups by recording filename |
| [0016: Database chat model policy](decisions/0016-database-chat-model-policy.md) | Accepted; implemented | Database-owned model policy with live catalog updates and insert-only migration |
| [0017: Coach recording recency](decisions/0017-coach-recording-recency.md) | Accepted; implemented and evaluated locally | Scope latest calls and return dated utterance evidence |
| [0018: Coaching model evaluations](decisions/0018-coach-model-evaluations.md) | Accepted; first comparison completed | Repeatable local model checks with private evidence and JUnit |
| [0019: Service observability](decisions/0019-observability.md) | Accepted; service-side implementation complete | Shared OpenTelemetry, OpenInference and correlated Sentry errors |
| [0020: Live database telemetry settings](decisions/0020-live-database-telemetry-settings.md) | Accepted; implemented | Database-first OpenTelemetry export, sampling and per-service live reload |
| [0021: Scheduled jobs](decisions/0021-scheduled-jobs.md) | Accepted; implemented | Durable task identity, current authorization, recovery and job dashboard |

## Working agreement

Use [the template](decisions/template.md) for choices affecting service boundaries, persistence, identity, operations, or tool behavior. Small implementation details belong in code and PR descriptions.

Create a proposed record before implementation; mark it accepted when the choice is agreed. Record actual implementation separately from intent. For past decisions, cite commits and code, and label reconstructed rationale as inference. Keep accepted records stable; supersede them with a linked record when direction changes.

Update the roadmap's status and validation evidence in the same change that completes an item. Runbooks describe repeatable operations; private credentials and production evidence belong outside Git. The initial review is dated **2026-09-07**, against `34d65e2` plus the accompanying cleanup.
