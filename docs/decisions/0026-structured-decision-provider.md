# 0026: Evaluate an API-owned structured decision provider

- Status: Proposed; not implemented
- Recorded: 2026-09-18
- Decision date: Pending
- Evidence: [Jev research and implementation plan](../plans/jev-integration.md); `CoachCheckinService`, `CoachTranscriptProcessingService`, and the coaching evaluation runner at `b14490e`
- Extends: [0004: Agent service boundary](0004-agent-service-boundary.md)

## Context

Coaching retrieval currently combines SQL scope, vector ranking, exercise hints and timestamped evidence. Exercise transitions and exact supporting-utterance selection remain quality gaps. Jev offers constrained probabilistic decisions; its output contract differs from the chat interface. The linked plan records primary sources, vendor claims, current code evidence and uncertainty.

## Decision

Propose an optional API-local evidence-reranking capability with a TypeSafe HTTP adapter, a deterministic fake and baseline fallback. Evaluate it offline and in shadow mode before any Active promotion. Keep Jev out of the chat catalog and keep generation, embeddings, authorization and durable actions unchanged. Use encrypted database-first settings, versioned policy, bounded calls and metadata-only telemetry. Speaker roles continue to follow [stereo source evidence](0025-stereo-coach-attribution.md).

## Alternatives

- Retain current ranking and improve deterministic retrieval: the default and rollback path; remains preferable if Jev's measured benefit is insufficient.
- Use the existing chat model for the same decision rubric: a useful optional evaluation comparator, with no assumed accuracy or cost disadvantage.
- Add Jev as a chat model or replace journal extraction: incompatible with its lack of free-text generation.
- Call TypeSafe directly from Worker or a new microservice: unnecessary provider/configuration spread for the first API-local use case.

## Consequences

Potentially better evidence selection comes with additional latency, cost and a new data processor. Valid output types do not establish truthful classifications or secure decisions. Account terms must be established before private inputs are transmitted. Domain quality, fallback and rollback require explicit evaluation; type safety alone is not a promotion criterion.

## Delivery and verification

Only research and documentation exist in this change. The [plan](../plans/jev-integration.md) specifies API seams, settings, staged delivery, tests, proposed quality/latency/cost gates and effort. Track acceptance in the [roadmap](../plans/roadmap.md#jev-structured-decisions--proposed-2026-09-18). A later implementation must provide evidence before this record can be marked accepted or implemented; optional transcript tagging needs its own evaluation and persistence work.
