# 0004: Centralize agent providers and use a single tool registry

- Status: Accepted direction; runtime model catalog implemented, provider/tool migrations pending
- Recorded: 2026-09-07
- Evidence: `AgentChatService`, `ToolAccessService`, `AssemblyAiTranscriptionService`, journal request/response consumers

## Decision

Make `PersonalAgent` the generic AI gateway. The maintainer explicitly confirmed this boundary on 2026-09-07: swapping AI providers should require implementation changes only in the API project. Keep pure domain logic separate from provider implementation. It already owns chat, journal parsing, and embeddings. Move AssemblyAI credentials and its provider adapter into the API service. Keep background delivery, retries, and job triggering in the worker. API ownership does not require every operation to be synchronous HTTP: use typed bus contracts for background work and HTTP for submission/status where appropriate.

For transcription, submit an upload ID and server-resolved subject context, persist a provider job ID, and return a durable job reference. Resume polling or process validated callbacks through the API-owned adapter. Do not hold one HTTP request open throughout transcription or send audio bytes through the bus. Define timeout, retry, idempotency, cancellation, and completion semantics before migration.

Replace duplicated tool lists with one registry containing stable key, description, argument schema/function factory, role defaults, availability, and side-effect classification. Bind actor/subject on the server. Build chat functions and the admin catalog from the same registrations. Retain invocation logging and enforce authorization at execution as well as discovery. Database rows configure permitted capabilities; they do not define arbitrary executable code.

## Alternatives and consequences

Keeping provider adapters in each service simplifies individual implementations but spreads credentials and policy. A new router microservice would duplicate the API's existing role. A generic plugin runtime is premature: start with typed C# registration, then adapt MCP tools to it.

The API service becomes a larger availability boundary; background work needs durable submission and restart recovery. The transcription migration must move configuration scopes and preserve in-flight jobs.

## Delivery and verification

Neither migration is implemented by this record. First add registry parity tests, then demonstrate a provider stub completing a persisted transcription job after an API restart without duplicate submission. Existing role restrictions, speaker-review behavior, and data ownership must remain intact.

## Provider independence contract

Use API-local capability interfaces and neutral request/result contracts. Provider SDK types, vendor error payloads, API keys, model names, polling protocols, and configuration schemas stay behind API adapters. Worker owns domain processing and delivery, Web/Mobile own presentation, and Contracts owns shared wire DTOs. A catalog may expose opaque capability/model choices for the UI, but clients must not branch on vendor names. Scheduled task execution now sends no model choice and uses the API-selected default; contract tests cover a changed default.

Provider-independent does not mean every model produces interchangeable embeddings. Define an embedding-space identifier and compatibility/version policy; API owns model/dimension selection and re-embedding coordination. Existing Worker-owned `vector(1536)` schema requires a one-time ownership/contract migration before future provider swaps can remain confined to API. Never mix old stored vectors with a new model's query vectors just because their dimensions match.

Provider replacement acceptance test: exercise the same domain contract against two API adapters (one may be a deterministic fake), with no changes to clients, worker logic, or wire DTOs. Document any capability mismatch explicitly; do not hide a breaking domain change as a provider swap.
## Runtime model catalog

The maintainer confirmed on 2026-09-07 that chat clients should retrieve available models from our API at runtime. The Web app calls `GET /api/models`. The API now refreshes provider availability through an API-local adapter, intersecting it with reviewed chat/tool policy. Labels and policy still load from configuration at startup. Keep the catalog contract owned by API, with opaque IDs, labels, and a default. The API may derive entries from local/database policy or provider discovery; clients must not call vendor model-list endpoints or hard-code vendor defaults. Provider-derived catalogs need chat-capability filtering, caching, timeout/fallback behavior, and policy validation before being offered to users. Do not expose every provider model as if it supports the same chat/tools contract.

Implemented in the runtime-model-catalog change: cached OpenAI inventory, timeout/fallback, a single default, new-session validation, and stable model identity for saved conversations. See [behavior and limitations](../runbooks/runtime-chat-models.md). This completes model discovery only; the transcription adapter and shared tool registry remain pending.
