# Runtime chat models

`GET /api/models` is the model-discovery contract for Web and other clients. It returns `{ "models": [{ "id": "...", "displayName": "...", "isDefault": true }] }`. Clients treat IDs as opaque. Omit `modelId` when creating a session to use the API's current default.

## Database policy

Active `Shared` and `Api` `ChatModels:*` rows in `app.configuration_settings` are the sole production policy source. Api rows override matching Shared keys. Appsettings and environment model lists are not consulted. Bootstrap still uses `DATABASE_URL` and `CONFIG_ENCRYPTION_KEY`, like other database settings. Synthetic mode explicitly supplies a fixed test model instead.

On API startup, an insert-only migration initializes model entries if no database model rows exist. Existing entries, including inactive ones, prevent seeding the model list. The embedded `Configuration/chat-model-policy.seed.json` is initial migration data, never a runtime fallback. A `ChatModels:PolicyInitialized` marker prevents restoring defaults if all model rows are later removed. Do not remove that marker to disable models; deactivate model ID rows instead. Startup migration preserves existing values, activity, scopes, and defaults and supports settings tables with or without `updated_at`.

Fresh policy seeds include GPT-5.5, GPT-5.6 Luna/Terra/Sol, and GPT-6 Astra alongside earlier models. Existing database policies are not automatically expanded. Reviewed support: [GPT-5.5](https://developers.openai.com/api/docs/models/gpt-5.5), [Luna](https://developers.openai.com/api/docs/models/gpt-5.6-luna), [Terra](https://developers.openai.com/api/docs/models/gpt-5.6-terra), [Sol](https://developers.openai.com/api/docs/models/gpt-5.6-sol), [Astra](https://developers.openai.com/api/docs/models/gpt-6-astra). Provider inventory still determines account availability; documentation review is not live inference verification.

Edit policy in **Settings → Database settings**, filtering for `ChatModels`. Each indexed model has `Models:N:Id`, `Models:N:DisplayName`, and `Models:N:IsDefault` entries. Change an existing ID/label to another reviewed model, or deactivate its ID row to remove that choice. An active Shared ID may still supply the value when an Api ID is inactive. To add more slots than exist, insert additional indexed rows through an authorized database migration; the generic editor only edits existing rows. No model choices are hardcoded in Web.

The API reads policy on every catalog request. Changes invalidate the provider cache immediately for the next request; no API restart is needed after an edit. Reload the chat page to refresh its browser-held choices, then create a chat. Existing sessions keep their selected model. Missing/empty policy yields no model choices and prevents new chat creation; invalid policy or database failure fails the lookup rather than reviving old policy or appsettings defaults.

## Discovery and caching

`IChatModelDiscovery` calls the provider model-list endpoint. `ChatModelCatalog` intersects those IDs with the database's reviewed chat/tool-compatible IDs. OpenAI's [model inventory](https://developers.openai.com/api/reference/resources/models/methods/list) does not provide endpoint/tool capability guarantees, so unknown provider models still require review before policy inclusion.

| Database key | Initial/default value | Behavior |
| --- | --- | --- |
| `ChatModels:DiscoverFromProvider` | `true` | Set false to serve database policy without provider discovery |
| `ChatModels:RefreshIntervalSeconds` | `300` | Availability-cache lifetime, 1–86400 seconds |
| `ChatModels:FailureRetrySeconds` | `30` | Retry delay after provider failure, 1–3600 seconds |
| `ChatModels:DiscoveryTimeoutSeconds` | `5` | Provider timeout including retries, 1–60 seconds |

Concurrent lookups share a refresh. Each call reads the current database policy under the catalog lock. Provider failures retain the last successful result only for the same policy; otherwise fallback is restricted to the new database policy. Successful empty provider results remain authoritative. Provider fallback cannot prove a subsequent inference request will succeed. Database/policy errors propagate and never take this provider-fallback path. Cancelled requests do not update the snapshot.

If choices stop at an older model, inspect the active database policy and discovery warnings. Adding names to appsettings has no effect. Ensure this version of the API is deployed once; subsequent database edits require only a picker reload. The source transcript retrieval fix in this PR remains independent and requires no reprocessing.

## Verification

`DatabaseChatModelPolicyTests` uses PostgreSQL and the existing settings editor store to verify insert-only migration, current/older table schemas, scope precedence, inactive and deleted entries, policy edits, cache invalidation, provider failure, and invalid policy. Catalog tests cover timeout, cancellation, concurrency and availability changes. HTTP tests verify newer model session creation; Web tests render the picker and settings lifecycle labels. Providers are test doubles; no private account inventory or paid model-quality evaluation is claimed.

```powershell
dotnet test PersonalAgent.Tests/PersonalAgent.Tests.csproj
dotnet test PersonalAgent.Web.Tests/PersonalAgent.Web.Tests.csproj
```
