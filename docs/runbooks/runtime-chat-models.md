# Runtime chat models

`GET /api/models` remains the only model-discovery contract for Web and other clients. It returns `{ "models": [{ "id": "...", "displayName": "...", "isDefault": true }] }`. Model IDs are opaque client values. Omit `modelId` when creating a session to use the API's current default.

The API calls OpenAI's model-list endpoint on demand through `IChatModelDiscovery`. `IChatModelCatalog` owns eligibility, labels, defaults, caching, and fallback. A provider replacement changes the adapter and its API registration, without changing this wire contract or client code.

## Policy and configuration

`ChatModels:Models` is the ordered list of reviewed chat/tool-compatible model IDs and labels. Discovery intersects that list with the provider's available IDs. It does **not** automatically offer new or unknown provider models. OpenAI's [model inventory](https://developers.openai.com/api/reference/resources/models/methods/list) provides identity/availability information rather than chat/tool capability guarantees; name-prefix guessing would expose incompatible models. Review support before adding a model to API policy.

The existing `ChatModels:Models` settings and these optional API settings can come from normal configuration, including active `Api` rows in `app.configuration_settings`:

| Setting | Default | Behavior |
| --- | --- | --- |
| `ChatModels:DiscoverFromProvider` | `true` | Set `false` to serve configured policy without provider discovery |
| `ChatModels:RefreshIntervalSeconds` | `300` | Successful lookup cache duration; 1–86400 seconds |
| `ChatModels:FailureRetrySeconds` | `30` | Retry delay after discovery failure; 1–3600 seconds |
| `ChatModels:DiscoveryTimeoutSeconds` | `5` | Total discovery wait, including SDK retries; 1–60 seconds |

The database configuration provider still loads at startup. Changing the reviewed list, credentials, or these options requires an API restart. Provider availability refreshes while the API runs; no Web or Worker deployment is needed. Live editing of database policy is separate future work.

## Runtime behavior

- The first lookup refreshes inventory; later requests reuse it until expiry. Simultaneous callers share one refresh. There is no background polling when the app is idle.
- The configured default is preferred when available; otherwise the first available configured model becomes the single default. Blank/duplicate IDs are removed. An empty configuration retains the previous API-owned `gpt-4o-mini` fallback.
- On timeout/provider failure, use the last successful list, or configured models before the first successful lookup. Retry after the failure delay. Fallback has no maximum age and cannot prove the provider will accept a subsequent chat request; watch discovery warnings for persistent failures.
- A successful lookup with no eligible models is authoritative: return an empty model list and HTTP 503 Problem Details when creating a session. A later failure does not resurrect previously unavailable choices.
- Cancellation propagates; cancelled requests do not overwrite the cache. Provider errors are logged by type, without provider response bodies or keys.
- New sessions and journal parsing jobs use the current selection. Saved conversations retain their model ID, even if it is later removed from the catalog. A retired provider model can fail at execution; there is no silent model switch. Start a new conversation to choose another model. Legacy session state without an ID uses the current default.

## Verification

The API test suite exercises refresh/default changes, eligibility filtering, empty inventories, fallback/backoff, cancellation, timeout, concurrent requests, the SDK's model-list HTTP request, and transcript model preservation. All provider traffic in these tests uses fakes; no paid inference or live credentials are required.

```powershell
dotnet test PersonalAgent.Tests/PersonalAgent.Tests.csproj
```

The existing vector integration test requires local Docker. For manual catalog checks use `PersonalAgent/ChatModels.http` with your local internal API key.
