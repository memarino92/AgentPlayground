# 0026: Evaluate an API-owned structured decision provider

- Status: Proposed; not implemented
- Recorded: 2026-09-18
- Decision date: Pending
- Evidence: [Jev research and implementation plan](../plans/jev-integration.md); `AgentChatService`, `AgentToolRegistry`, `AgentToolBinder`, `CoachCheckinService`, and the coaching evaluation runner at `b14490e`; maintainer request for general automation and home-control routing on 2026-09-18
- Extends: [0004: Agent service boundary](0004-agent-service-boundary.md)

## Context

Main chat currently selects authorized tools through the agent loop. The maintainer wants to test general automation and home-control commands through a faster front-end decision layer. Coaching retrieval also has exercise-transition and exact-utterance quality gaps. Jev offers constrained probabilistic decisions; its output contract differs from chat. The plan records sources, current code evidence and uncertainty for both experiments.

## Decision

Propose two independently gated API capabilities sharing a TypeSafe HTTP adapter: pre-chat tool routing and evidence reranking. Test automation with stateful simulated devices first. Clear supported commands may execute through existing bound functions; incomplete or complex requests receive clarification or main-chat handoff. Add durable operation identity and outcome tracking before direct mutations, since current chat persistence alone cannot prevent duplicate effects. Keep authorization in the existing execution wrapper and Jev out of the chat catalog. Use encrypted database-first settings, separate capability modes, versioned policy, bounded calls and metadata-only telemetry. Speaker roles continue to follow [stereo source evidence](0025-stereo-coach-attribution.md).

## Alternatives

- Retain current ranking and improve deterministic retrieval: the default and rollback path; remains preferable if Jev's measured benefit is insufficient.
- Use the existing chat model for the same decision rubric: a useful optional evaluation comparator, with no assumed accuracy or cost disadvantage.
- Keep all tool execution in the main chat loop: the baseline; compare Jev suggestions and direct dispatch against it, including fallback overhead and complete argument correctness.
- Add Jev as a chat model or replace journal extraction: incompatible with its lack of free-text generation.
- Call TypeSafe directly from Worker or a new microservice: unnecessary provider/configuration spread for the first API-local use case.

## Consequences

Direct commands may avoid chat-model cost and latency; suggestions and reranking add overhead that must earn its place through measured quality. Home integration adds device inventory, argument validation and external-action recovery work. The actual platform is unspecified, so start with a fake. Valid output types do not establish correct intent or permission. Account terms must be established before private inputs are transmitted. Domain quality, fallback and rollback require explicit evaluation; type safety alone is not a promotion criterion.

## Delivery and verification

Only research and documentation exist in this change. The [plan](../plans/jev-integration.md) specifies API seams, settings, staged delivery, tests, proposed quality/latency/cost gates and effort. Track acceptance in the [roadmap](../plans/roadmap.md#jev-structured-decisions--proposed-2026-09-18). A later implementation must provide evidence before this record can be marked accepted or implemented; optional transcript tagging needs its own evaluation and persistence work.
