# 0027: Jev selection and direct dispatch across the current authorized tool catalog

- Status: Accepted; implemented
- Recorded: 2026-09-19
- Decision date: 2026-09-19
- Evidence: maintainer requested every existing tool/integration after live clock routing succeeded; `AgentToolRegistry`, `JevToolRoutingCatalog`, `JevRequestRouter`, and routing integration tests
- Extends: [0026](0026-structured-decision-provider.md)

## Context

The first router considered seven local tools but excluded the five Tavily MCP tools. Notifications and scheduling were already eligible for suggestions. Live testing confirmed the clock fast path; HTTP success alone did not reveal routing outcomes in Railway logs.

## Decision

Include all seven local tools and the five currently integrated Tavily tools in Jev selection when bound for the actor and available. Send application-owned descriptions for known Tavily tools, never remote MCP descriptions or schemas. Unknown remote tools remain under normal chat orchestration until their routing metadata is reviewed. Shadow observes, Suggest advises chat, and DirectReadOnly advises chat except for the enrolled clock adapter. The maintainer clarified that direct execution is required: add explicit `DirectTools` mode for source-bound argument adapters across the current catalog.

For supported direct command forms, code copies source text, normalizes explicit relative durations and validates arguments against the bound function schema, then invokes the existing authorization wrapper without chat inference. Unsupported forms or schema changes fall back to chat before execution. No inline retry or post-invocation chat fallback is added. Scheduled mutations reuse durable jobs; repeated chat submissions are not deduplicated. Tool selection never grants permission. Preserve the complete authorized tool set for multi-step requests and follow-up decisions. Integrations that are not chat tools (for example telemetry and transcription infrastructure) do not become callable administration tools.

Add metadata-only informational event 2604 for mode, outcome, validated selected tool, candidate count and elapsed routing time. OpenInference uses policy `pre-chat-v2`; existing Sentry error reporting remains unchanged.

## Alternatives

- Forward all MCP descriptions: simpler but could disclose remote configuration or instruction content to another provider.
- Let Choice generate arbitrary arguments: it selects from supplied options. Use explicit source-bound adapters instead; unsupported natural-language fields still need chat. The [TypeSafe cookbook](https://docs.typesafe.ai/cookbooks/function_calling) establishes closed-set function selection, not free-text generation.
- Hide all other tools after selection: would prevent prerequisites and multi-step requests and over-trust a single probabilistic decision.

## Consequences

Supported commands skip chat inference and return actual tool results, including raw search evidence. Each newly integrated remote tool needs application-owned routing metadata and a direct adapter before dispatch. Selection adds a provider call and does not establish a quality or cost improvement for chat-mediated execution. Thresholds remain unchanged. DirectTools is opt-in because notifications, sync and scheduling have side effects; DirectReadOnly retains its original semantics.

## Delivery and verification

All 308 API tests pass, including actual registered local argument schemas, all five direct MCP adapters, unsupported grammar/schema rejection, a direct reminder persisted in PostgreSQL with actor/source identity, and direct notification chat/embedding bypass. Existing tests cover authorization filtering, remote-description privacy, metadata-only logs and chat fallback. These tests prove wiring, not live selection accuracy or notification delivery. Live testing of the expanded catalog follows deployment; [the runbook](../runbooks/jev-routing.md) describes exact command forms and expected evidence. Recovery rehearsal and general durable action identities remain deferred.
