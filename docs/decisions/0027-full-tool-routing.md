# 0027: Jev selection across the current authorized tool catalog

- Status: Accepted; implemented
- Recorded: 2026-09-19
- Decision date: 2026-09-19
- Evidence: maintainer requested every existing tool/integration after live clock routing succeeded; `AgentToolRegistry`, `JevToolRoutingCatalog`, `JevRequestRouter`, and routing integration tests
- Extends: [0026](0026-structured-decision-provider.md)

## Context

The first router considered seven local tools but excluded the five Tavily MCP tools. Notifications and scheduling were already eligible for suggestions. Live testing confirmed the clock fast path; HTTP success alone did not reveal routing outcomes in Railway logs.

## Decision

Include all seven local tools and the five currently integrated Tavily tools in Jev selection when bound for the actor and available. Send application-owned descriptions for known Tavily tools, never remote MCP descriptions or schemas. Unknown remote tools remain under normal chat orchestration until their routing metadata is reviewed. No new configuration is required: Shadow observes, Suggest advises chat, and DirectReadOnly advises chat except for the enrolled clock adapter.

Chat supplies free-text, time, URL and other arguments and invokes the existing bound wrappers. Tool selection never grants permission. Preserve the complete authorized tool set for multi-step requests and follow-up decisions. Integrations that are not chat tools (for example telemetry and transcription infrastructure) do not become callable administration tools.

Add metadata-only informational event 2604 for mode, outcome, validated selected tool, candidate count and elapsed routing time. OpenInference uses policy `pre-chat-v2`; existing Sentry error reporting remains unchanged.

## Alternatives

- Forward all MCP descriptions: simpler but could disclose remote configuration or instruction content to another provider.
- Directly execute every selected tool: Choice supplies no free-text arguments, so this cannot implement the existing tool contracts. Argument generation remains in chat.
- Hide all other tools after selection: would prevent prerequisites and multi-step requests and over-trust a single probabilistic decision.

## Consequences

Web requests now benefit from selection guidance. Each newly integrated remote tool needs an application-owned description before Jev can suggest it. Selection adds a provider call and does not establish a quality or cost improvement for chat-mediated execution. Thresholds and fallback remain unchanged.

## Delivery and verification

All 288 API tests pass, including coverage of all 12 tools in Shadow, Suggest and DirectReadOnly; authorization filtering; remote-description privacy; metadata-only logs; and chat-loop invocation/persistence with synthetic scheduling and web handlers. These tests prove wiring, not live selection accuracy or notification delivery. Live testing of the expanded catalog follows deployment; [the runbook](../runbooks/jev-routing.md) describes expected evidence.
