# 0026: Evaluate an API-owned structured decision provider

- Status: Proposed; not implemented
- Recorded: 2026-09-18
- Decision date: Pending
- Evidence: [Jev research and implementation plan](../plans/jev-integration.md); `AgentChatService`, `AgentToolRegistry`, `AgentToolBinder`, `CoachCheckinService`, and the coaching evaluation runner at `b14490e`; maintainer request for general automation and home-control routing on 2026-09-18
- Extends: [0004: Agent service boundary](0004-agent-service-boundary.md)

## Context

Main chat currently selects authorized tools through the agent loop. The maintainer wants pre-chat routing, LLM-delegated structured outputs/actions through Jev and deterministic binding, and judging of tool-call use. Automation includes home-control experiments. Coaching retrieval also has exercise-transition and exact-utterance quality gaps. Jev offers constrained decisions rather than general text generation. The plan records source evidence and uncertainty for each experiment.

## Decision

Propose independently gated API capabilities sharing a TypeSafe HTTP adapter: pre-chat routing, LLM-callable structured decisions/delegated actions, tool-call judging, and evidence reranking. Direct and delegated automation share deterministic argument binding, existing authorized functions, and durable operation identity/outcome tracking. Strict delegated mode hides enrolled leaf tools from the LLM to prevent bypass. The judge observes concrete proposed calls and recorded outcomes first; enforcement requires independent evaluation and explicit enrollment. Keep Jev out of the chat catalog. Use encrypted database-first settings, separate modes, versioned policy, bounded calls and metadata-only telemetry. Speaker roles continue to follow [stereo source evidence](0025-stereo-coach-attribution.md).

## Alternatives

- Retain current ranking and improve deterministic retrieval: the default and rollback path; remains preferable if Jev's measured benefit is insufficient.
- Use the existing chat model for the same decision rubric: a useful optional evaluation comparator, with no assumed accuracy or cost disadvantage.
- Keep all tool execution in the main chat loop: the baseline; compare Jev suggestions and direct dispatch against it, including fallback overhead and complete argument correctness.
- Use schema-constrained LLM tool calls with deterministic validation alone: a necessary comparator for delegated Jev calls; measure whether Jev improves semantic decisions enough to justify another model hop.
- Add Jev as a chat model or replace journal extraction: incompatible with its lack of free-text generation.
- Call TypeSafe directly from Worker or a new microservice: unnecessary provider/configuration spread for the first API-local use case.

## Consequences

Direct commands may avoid chat-model cost; delegation, judging and reranking add overhead. Deterministic binding can prevent structurally invalid requests from reaching enrolled handlers, but cannot guarantee correct actions or a valid outer LLM envelope. Free-text and arbitrary numeric fields need separate validated sources. A Jev selector and judge can share errors, so judging needs independent labels. Home integration adds inventory and external-action recovery work; start with a fake. Account terms must be established before private inputs are transmitted. Type safety alone is not a promotion criterion.

## Delivery and verification

Only research and documentation exist in this change. The [plan](../plans/jev-integration.md) specifies API seams, settings, staged delivery, tests, proposed quality/latency/cost gates and effort. Track acceptance in the [roadmap](../plans/roadmap.md#jev-structured-decisions--proposed-2026-09-18). A later implementation must provide evidence before this record can be marked accepted or implemented; optional transcript tagging needs its own evaluation and persistence work.
