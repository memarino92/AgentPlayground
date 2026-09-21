# 0029: One conversation with bounded context and typed presentation

- Status: Accepted; first continuous-chat/context/presentation slice implemented
- Recorded: 2026-09-21
- Decision date: 2026-09-21
- Evidence: maintainer request, AgentChatService, PostgresAgentSessionStore, Chat.razor, [vision](../plans/personal-assistant-vision.md)
- Extends: [0023](0023-query-navigation-and-chat-preferences.md)

## Context

Users currently select sessions and chat replays the entire selected transcript. Semantic memory stores some short user statements using substring heuristics. This does not provide bounded continuous conversational context or reliable topic resumption. Scheduled job conversations must remain immutable records.

## Decision

Provide one persistent default conversation per actor, role and subject. Keep legacy conversation links and optional history browsing. Bound recent context and automatically retrieve older, authorized evidence using lexical and semantic signals. Historical content is untrusted evidence, not higher-priority instructions or established facts. A classifier/episode pipeline remains a separately evaluated extension.

Persist a fresh-start boundary. Exclude earlier conversation and heuristic memories from automatic context after the boundary; retain stored records for explicit history viewing. This first slice does not introduce a separate confirmed-fact store, household sharing or deletion semantics.

Represent model-proposed commitment cards, checklists and clarification forms as validated typed presentation metadata. Store presentation with the assistant message, persist user interactions under current authorization and optimistic revision checks, and route clarification answers through normal chat. Commitment cards track user-confirmed intent; they do not themselves schedule notifications or implement the proposed durable goal engine.

## Alternatives

- Keep session selection: retained only for optional history/legacy links because it imposes organizational work.
- Send all history: rejected for unbounded cost and irrelevant context.
- Classifier-only recall: deferred because misclassification must not make evidence inaccessible.
- Rewrite frontend or execute generated JavaScript: unnecessary for the first three trusted components.

## Consequences

Context budgets and retrieval thresholds limit recall; ambiguous references require clarification. Initial semantic history coverage grows with indexed turns; lexical recall covers existing transcripts. Retain one actor/subject authorization boundary across retrieval and card updates. Serialize mutations across API instances so clear/model changes cannot race a turn. Test with synthetic providers; report live quality separately.

## Delivery and verification

Implemented: default continuity, bounded recent and lexical/semantic historical context, actor/role/subject scoping, persistent fresh-start boundaries, optional searchable history and typed commitment/checklist/clarification cards. Database and component tests cover isolation, provider fallback, service recreation, concurrency and stale updates. The [runbook](../runbooks/continuous-conversation.md) records verification and operational limits. Classifier/episode evaluation, a confirmed-fact store and durable follow-through remain future work in the [roadmap](../plans/roadmap.md).
