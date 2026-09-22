# 0032: Add OpenRouter and adopt provider-managed auto model routing first

- Status: Accepted; first slice implemented
- Recorded: 2026-09-22
- Decision date: 2026-09-22, maintainer requested OpenRouter support in the API and an auto-model router, with Jev as a candidate
- Evidence: `CompositeChatModelDiscovery`, `OpenRouterClientProvider`, `OpenAiAgentChatClientFactory`, and [runtime operations](../runbooks/runtime-chat-models.md)
- Extends: [0012: Live provider credentials](0012-live-provider-credentials.md), [0016: Database chat model policy](0016-database-chat-model-policy.md), and [0026: Structured decision provider](0026-structured-decision-provider.md)

## Context

The API already owns chat provider access behind `IChatClient`, exposes a database-approved runtime catalog, and persists opaque selected model IDs in sessions. OpenAI was the only generative provider. Jev routes structured tool decisions before chat, but it does not generate the assistant response. A model router must preserve Agent Framework tool calling, database eligibility, existing-session stability, live credential rotation, and the ability to compare explicit models.

OpenRouter exposes an OpenAI-compatible endpoint and the `openrouter/auto` model. Its documentation says Auto selects a downstream model from the request, reports the selected model, and applies conversation stickiness. Those are provider claims and behavior, not a quality/cost result for this application.

## Decision

Add OpenRouter as a second provider behind the existing Microsoft.Extensions.AI and Agent Framework boundary. Qualify its public catalog IDs with `openrouter:` and strip that prefix only inside the provider factory. Discover configured providers independently, merge their inventories, and retain healthy provider results when another provider fails. Keep database model policy authoritative.

Adopt `openrouter:openrouter/auto` as the first optional automatic router entry. Do not make it the default in existing databases or silently append it to administrator-owned policies. Continue using Jev for structured tool routing; defer Jev-assisted or application-owned model selection until a routing dataset can compare complete-turn quality, latency, privacy and cost.

## Alternatives

- Use Jev immediately to choose among chat models: deferred because the application has no routing labels or policy for choosing a model, and this adds a separate model call before every routed chat.
- Build a heuristic router in the API: deferred because prompt length or tool presence alone is not a demonstrated quality signal, and provider-managed routing supplies a testable baseline.
- Replace OpenAI with OpenRouter: rejected because embeddings and existing direct-model workflows still depend on OpenAI, while explicit side-by-side choices are useful for evaluation and rollback.
- Expose raw OpenRouter slugs: rejected because provider qualification avoids collisions and lets saved sessions resolve the correct endpoint without another persistence migration.

## Consequences

The API gains one optional credential and another external dependency. Auto routing improves operational flexibility but weakens version pinning: its downstream pool and economics can change independently of this repository. A provider inventory entry does not prove tool compatibility. Provider discovery is partially tolerant, while inference failures remain visible and do not silently switch the saved catalog selection to another provider.

OpenRouter accepts prompts, history, tool schemas and tool outputs, so its data-processing and retention settings must be reviewed before private use. Cost-tier, allowed-model constraints, explicit session IDs, routed-model UI disclosure, usage accounting and live quality evaluation remain follow-ups.

## Delivery and verification

The first slice includes live-reloadable encrypted credentials, OpenAI-compatible client dispatch, qualified discovery, merged partial-failure behavior, the Auto seed for fresh policies, deployment seeding support, unit tests, and operations documentation. The follow-up Settings slice adds authenticated first-time key creation and replacement, encrypted persistence with optimistic concurrency, immediate reload feedback, and masked browser behavior. No paid live request, privacy review, downstream-model disclosure, or model-quality/cost comparison is claimed. See [runtime chat models](../runbooks/runtime-chat-models.md).
