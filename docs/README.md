# Project knowledge

This repository is becoming an **agentic digital garden**: a personal application suite that turns conversations, work journals, and coaching notes into useful, retrievable knowledge and follow-up actions.

## Start here

- [Architecture](architecture.md): current responsibilities and boundaries.
- [Roadmap](plans/roadmap.md): ordered work, evidence, and acceptance criteria.
- [Local development](runbooks/local-development.md): current setup and known gaps.
- [Snapshot commands](runbooks/snapshot-commands.md): implemented export and guarded local restore.
- [Runtime chat models](runbooks/runtime-chat-models.md): API discovery, policy, and fallback behavior.
- [Database recovery](runbooks/database-recovery.md): full recovery procedure and remaining evidence.
- [Public release](runbooks/public-release.md): publication readiness and cleanup inventory.

## Decision register

| Record | Status | Purpose |
| --- | --- | --- |
| [0001: Database configuration](decisions/0001-database-configuration.md) | Accepted, retrospective | Record the encrypted configuration refactor |
| [0002: Actor and tool authorization](decisions/0002-actor-and-tool-authorization.md) | Accepted, retrospective | Record RBAC and its remaining boundaries |
| [0003: Local parity and recovery](decisions/0003-local-parity-and-recovery.md) | Accepted; first slice implemented | Use local rehearsal before adding another hosted environment |
| [0004: Agent service boundary](decisions/0004-agent-service-boundary.md) | Accepted; model catalog implemented | Centralize provider access and tool registration |
| [0005: Durable garden workflows](decisions/0005-durable-garden-workflows.md) | Proposed | Introduce measurable, resumable agent workflows |
| [0006: Personal assistant tooling](decisions/0006-personal-assistant-tooling.md) | Accepted; migrated | Keep generic skills and tooling in user configuration |
| [0007: MIT license](decisions/0007-mit-license.md) | Accepted | Permit broad reuse under standard MIT terms |

## Working agreement

Use [the template](decisions/template.md) for choices affecting service boundaries, persistence, identity, operations, or tool behavior. Small implementation details belong in code and PR descriptions.

Create a proposed record before implementation; mark it accepted when the choice is agreed. Record actual implementation separately from intent. For past decisions, cite commits and code, and label reconstructed rationale as inference. Keep accepted records stable; supersede them with a linked record when direction changes.

Update the roadmap's status and validation evidence in the same change that completes an item. Runbooks describe repeatable operations; private credentials and production evidence belong outside Git. The initial review is dated **2026-09-07**, against `34d65e2` plus the accompanying cleanup.
