# Jev pre-chat routing

Implemented 2026-09-19: an API-owned Choice adapter and pre-chat routing seam. Maintainer-driven production clock tests confirmed successful live TypeSafe requests and direct clock execution without creating a chat model. Observed provider HTTP durations were roughly 275–398 ms; these individual observations are not latency benchmarks or evidence of general routing accuracy. HTTP fixtures follow the [TypeSafe API reference](https://docs.typesafe.ai/api), checked 2026-09-19; they are documentation-based fixtures, not captured provider responses. Broader accuracy and cost remain unmeasured.

## Current behavior

After session ownership validation, the API binds the current authorized tools and routes the latest user message before creating the chat client or retrieving semantic memory. Scheduled tasks retain their existing path. Only local catalog descriptions, application-owned descriptions for known Tavily tools, their function names and the current message go to TypeSafe; no stored transcript, remembered content, source records, subject identifiers or remote MCP descriptions/schemas are included. A current message can itself contain private information.

Selection covers all 12 currently registered tools when enabled for the actor: mobile push, work-journal sync/search, coaching search, notification scheduling, agent-task scheduling, clock, and Tavily search/extract/crawl/map/research. Unknown remote tools remain available to chat but require reviewed routing metadata in `JevToolRoutingCatalog` before inclusion in Jev choices. Infrastructure integrations such as Sentry, telemetry and transcription do not expose new administrative chat tools.

| Mode | Behavior |
| --- | --- |
| `Off` | Existing chat; no decision-provider call. Default. |
| `Shadow` | Evaluate tool selection and record metadata; keep existing chat behavior. This is a bounded awaited call, so shadow adds latency. |
| `Suggest` | Add a server-validated tool-name suggestion to chat instructions. Chat independently supplies arguments and retains all authorized tools. No tool has already run. |
| `DirectReadOnly` | Execute the enrolled clock adapter for a complete standalone clock request; other selected tools are suggestions. |
| `DirectTools` | Execute a selected tool directly when its explicit request grammar and bound schema match; otherwise suggest it to chat. Includes notifications and scheduling. |

The clock adapter accepts narrow phrases such as “What time is it?”, “What time is it now?” and “Tell me the current date please.” It supplies an explicit null timezone and returns the actual tool result. Timezone requests, compounds, context-dependent requests, quoted commands and other unsupported forms cannot enter this adapter, even if the classifier confidently selects the clock. They continue through chat. `DirectTools` additionally enables the explicit forms below; `DirectReadOnly` remains clock-only. Existing authorization wrappers remain in use.

Direct calls use the bound `LoggingAIFunction`, including execution-time authorization and tool tracing. Their user/assistant turns are persisted in the ordinary session; they skip chat inference and semantic embedding work. Once invocation begins, an error propagates instead of re-entering chat and potentially repeating a call. No inline execution retry is added. Scheduled notifications/tasks use their existing durable job store. Request-level deduplication across repeated chat submissions is not implemented; inspect job state before retrying an interrupted scheduling request. Recovery rehearsal remains deferred.

## Direct tool requests

Set only `mode` to `DirectTools` in the existing Jev settings JSON, keep the key and `allowUserContent: true`, and wait 15 seconds. This mode can perform side effects through currently authorized tools. Jev must select the corresponding tool above both thresholds; the compiler copies source values and checks the bound function schema before invocation. It does not ask a chat model to fill arguments. TypeSafe's [function-calling cookbook](https://docs.typesafe.ai/cookbooks/function_calling) describes closed-set selection; the app's free text comes from explicit request spans instead.

| Tool | Supported direct example |
| --- | --- |
| Clock | `what time is it now?` |
| Mobile push | `notify me: drink water` |
| Reminder | `remind me in 5 minutes to drink water` |
| Future agent task | `schedule a task in 1 hour: check the weather` |
| Journal sync | `sync my work journal` |
| Journal search | `search my work journal for database migrations` |
| Coaching search | `search my coaching notes for yoke` |
| Web search | `search the web for dotnet releases` |
| Web research | `research: battery recycling` |
| Page extraction | `extract https://example.com/docs` |
| Site crawl | `crawl https://example.com` |
| Site map | `map https://example.com` |

These are supported command forms, not unrestricted natural-language argument extraction. Timing accepts positive integer seconds/minutes/hours/days up to one year; tomorrow/absolute times and timezone interpretation fall back to chat. Notification titles are `Notification` or `Reminder`; bodies/instructions are copied exactly. Scheduled tasks notify on completion. Search tools return their actual evidence, without an LLM-written summary. Coaching search uses recent ordering without filename/exercise filtering. URL tools accept one explicit HTTP(S) DNS URL without userinfo; Tavily applies its remaining defaults. The current [Tavily MCP source](https://github.com/tavily-ai/tavily-mcp/blob/main/src/index.ts) defines these parameter names; runtime schema mismatches fail closed to chat.

Other phrasings, missing arguments, unsupported constraints and low confidence fall back to normal chat. Jev does not gain access to tools disabled for the current role. Direct results are tool output, including remote text/error content, rather than generated success claims.

## Configuration

API startup inserts two `Api` rows into the existing encrypted configuration store without overwriting existing entries. Find `Jev` in Settings → database settings:

- `Jev:ApiKey`: encrypted, masked, replaceable and clearable. Initially empty.
- `Jev:Settings`: validated JSON. Initially:

```json
{
  "mode": "Off",
  "model": "jev-1.13.0",
  "timeoutMilliseconds": 750,
  "minimumProbability": 0.95,
  "minimumConfidence": 0.8,
  "allowUserContent": false
}
```

Only `Api` scope is read; no environment or Shared overrides are used. Settings and key load as one immutable in-memory snapshot every 15 seconds. Administrative saves validate syntax; invalid external edits or database failures retain the last valid snapshot. The editor displays stored values, not confirmed runtime state. Clearing the key, deactivating either row, or setting mode to Off disables routing after the next successful reload; in-flight calls keep their snapshot. The existing credential reload button applies to OpenAI/AssemblyAI, not Jev.

Before enabling live routing, verify provider access and the pinned model with synthetic inputs, confirm account terms, and evaluate selection quality. `allowUserContent` must be explicitly true before ordinary messages can leave the API. A key alone does not enable calls. Thresholds are provisional, not calibrated accuracy guarantees. This slice uses the existing settings editor and atomic updates; separate immutable configuration-history/promotion UI and per-instance acknowledgement are still follow-up work.

## Key-free verification

```powershell
dotnet test PersonalAgent.Tests/PersonalAgent.Tests.csproj --filter "FullyQualifiedName~Jev&FullyQualifiedName!~JevRuntimeDatabaseTests"
```

The synthetic Compose environment replaces the decision provider with `SyntheticToolDecisionClient`. It never contacts TypeSafe, even if a key is stored. Set mode to `DirectReadOnly` in its database settings and wait for reload, then ask “What time is it?” in a new or existing chat. The response comes from the authorized clock tool. The fake recognizes only three exact examples (clock, work-journal search and coaching-note search); all other messages select normal chat. Its choices test wiring, not model quality. Synthetic routing does not require a key or user-content consent because it is entirely local; mode still defaults Off.

With Docker available, run `JevRuntimeDatabaseTests` to verify insert-only defaults, encryption/masking, live reload, invalid-edit retention and disabling. After Docker became available on 2026-09-19, the full API suite passed all 276 tests, including this database test. Rebuilt API/Web/Worker containers became healthy and all 22 synthetic smoke checks passed. Browser verification confirmed the masked key control, saving DirectReadOnly mode, a direct clock response, and its persistence after reload. The demo mode was restored to Off afterward; no real API key was entered or provider call made.

Local verification on 2026-09-19 passed 184 API tests excluding PostgreSQL-dependent classes, including 56 Jev cases, plus all 56 Web tests. This includes the real chat service, authorized function wrapper, fake HTTP transport, settings validation and existing Sentry error pipeline. These deterministic results establish wiring and failure behavior, not provider quality.

## Bounds and failures

The adapter uses the fixed HTTPS TypeSafe endpoint, disallows redirects, sends credentials per request and has no inline retries. It limits concurrent calls to four, the current message to 8,000 characters, choices to 255 including fallback, and response bodies to 64 KiB. The total timeout includes response-body reading. Capacity exhaustion, oversized inputs, provider errors, transport failures and invalid responses preserve normal chat. Caller cancellation propagates.

Responses must match the pinned model, contain the expected Choice answer and every offered option exactly once, have finite probabilities/confidence in range, sum to one within 0.001, and select a highest-probability option. The router separately checks the selected tool against the current bound list and applies probability and confidence thresholds independently.

HTTP failures establish a bounded cooldown (honoring delta `Retry-After` within 1–300 seconds; default 60 seconds for 401/422 and 15 otherwise). Timeouts, transport and contract failures cool down for 15 seconds. Calls already in flight may complete. This is local throttling, not a distributed circuit breaker. Key changes use the new snapshot on subsequent calls; a current cooldown can delay their use.

## Instrumentation

The existing `agent.run` span contains `agent.route` (`CHAIN`) and `decision.choose` (`LLM`), followed by either the existing `tool.invoke` (`TOOL`) or ordinary chat/retrieval spans. OpenInference records the configured model and provider-reported usage when available. Absent usage stays unknown.

The trace privacy allowlist retains `routing.mode`, `routing.outcome`, and `decision.policy=pre-chat-v2`, alongside existing tool, model, error and token fields. No message, arguments, provider response, key or subject identifier is attached. Span duration provides routing/provider latency. Existing `agent.operation.duration` and `agent.token.usage` metrics distinguish `operation.path=pre_chat` from `standard`; routing duration also carries mode/outcome. These metrics are derived from sampled spans and are not billing records.

Informational log event 2604 records `Jev routing: mode=...; outcome=...; tool=...; candidates=...; elapsedMs=...` for completed routing decisions, including disabled/fallback outcomes. The tool and finite probability/confidence scores are populated after selection validates, including when thresholds reject it. The log includes the configured minimums and a gate of probability, confidence, probability_and_confidence, or invalid_selection. This distinguishes abstention from argument-binding fallback without lowering thresholds. No user text, arguments, credentials or remote descriptions are logged. This provides Railway diagnostics without requiring a sampled trace; informational events do not create Sentry issues.

## Expanded catalog verification

All 308 API tests passed after adding full current-catalog selection and direct argument adapters. Coverage includes a PostgreSQL job created through the direct reminder path, chat/embedding bypass for a direct notification, MCP argument/result handling, unsupported grammar/schema rejection, metadata privacy, and existing authorization/failure tests. Live expanded-catalog accuracy and device delivery are still unverified. Use `DirectTools` to enable the additional direct adapters; existing modes retain their behavior.

For a direct reminder, look for `outcome=direct; tool=schedule_notification`, its tool invocation, and a persisted job visible on the job dashboard, with no chat-model creation. Scheduling does not prove device delivery. Web search similarly records `outcome=direct; tool=tavily_search` and returns tool evidence. `suggest` means the arguments could not be bound directly or the mode only allows suggestions. Low-confidence decisions produce `abstained` and retain ordinary chat. Tool permissions remain managed in the existing tool catalog.

Sentry uses the existing logger integration and its trace/span correlation. Event 2601 is a settings reload failure, 2602 is an HTTP 401/422 contract/credential rejection, and 2603 is an invalid decision response. Raw exception and provider response bodies are deliberately omitted. Expected capacity/uncertainty fallbacks are trace outcomes, not Sentry exceptions. Backend receipt and dashboards have not been verified for this slice.

## Remaining experiments

Live contract probes and paired held-out evaluations precede promotion. The broader [plan](../plans/jev-integration.md) still includes simulated home inventory, additional typed argument adapters, action identity, delegated decision/action tools, independent pre/post-call judging and evidence reranking. They are not implemented by this routing slice. Recovery rehearsal work is deferred at the maintainer's request.
