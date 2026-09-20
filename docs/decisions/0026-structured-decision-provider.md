# 0026: Evaluate an API-owned structured decision provider

- Status: Accepted for the first pre-chat routing slice; broader experiments remain proposed
- Recorded: 2026-09-18
- Decision date: 2026-09-19 (maintainer requested key-free implementation, prioritizing pre-chat tool routing and instrumentation)
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

The first implementation adds a Choice HTTP adapter, encrypted database-first settings with validated snapshots, authorized pre-chat suggestions, bounded shadow evaluation and a narrow clock-only direct adapter. Direct execution uses the existing bound function wrapper and saves an ordinary chat turn while skipping chat inference and embeddings. Other tools remain under chat orchestration; no production mutation is directly routed. Synthetic providers and fake HTTP responses exercise the path without a key. OpenInference spans and existing Sentry error capture carry operational metadata only.

This bounded slice deliberately reuses the existing settings editor and does not yet implement immutable configuration promotion history, simulated home actions, durable action records, delegated tools, judging or reranking. No live quality, latency or cost benefit has been established. The [runbook](../runbooks/jev-routing.md) records exact behavior and verification limits. The [plan](../plans/jev-integration.md) remains the proposed direction for subsequent experiments; track acceptance in the [roadmap](../plans/roadmap.md#jev-structured-decisions--first-routing-slice-implemented-2026-09-19).
