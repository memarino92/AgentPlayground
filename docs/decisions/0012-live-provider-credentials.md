# 0012: Reload provider credentials at request boundaries

- Status: Accepted; first provider-credential slice implemented
- Recorded: 2026-09-11
- Decision date: 2026-09-11, maintainer requested refactoring existing settings to avoid restarts
- Evidence: `PostgresConfigurationSource`, `LiveOptions`, `OpenAiClientProvider`, `TranscriptionServiceCollectionExtensions`
- Extends: [0011](0011-existing-settings-editor.md)

## Context

Legacy database configuration loads only during startup. A provider reload alone cannot update options cached by `IOptions<T>` or SDK clients captured by singleton services. Messaging, authentication, schema names and embedding dimensions also have lifecycle constraints beyond credential replacement.

## Decision

Begin with OpenAI and AssemblyAI API keys in Shared/Api scopes. Poll in API every 15 seconds and expose an authenticated manual reload. The database provider reads only supported keys, resolves Shared/Api precedence, validates changed effective credentials and atomically publishes a replacement dictionary. Unrelated values remain pinned to startup. Invalid credentials or database read failures retain the previous dictionary. Explicit credential environment variables retain precedence.

Bridge existing `IOptions<T>` consumers to `IOptionsMonitor<T>` only for the affected option types. OpenAI client providers cache by credential and construct replacement clients for new operations; already-created clients remain usable for outstanding requests. Chat, embeddings, journal parsing and model discovery all use this path. AssemblyAI's named HTTP client reads current options when each provider request starts; one upload/submit pair shares the same HTTP client.

The UI distinguishes supported live credentials from settings still requiring restart. It reports the API reload check and override names without credentials. Syntax validation establishes printable token shape, not provider validity. Rotate keys within the same provider account, since existing transcription IDs belong to that account.

## Alternatives

- Reload every database value: unsafe while consumers still capture startup configuration.
- Recreate the whole service container: risks outstanding requests and messaging state.
- Rebuild SDK clients on every request: unnecessary churn; retain a credential-keyed client until rotation.

## Consequences

The first two provider credentials no longer require API restart. This is not the completed migration of every setting. Remaining work includes Tavily/MCP reconnection, Firebase client replacement, GitHub settings and Worker lifecycle, OAuth options, coordinated internal API/signing-key rotation, and messaging/storage infrastructure. Their restart labels remain until corresponding consumers and failure paths are implemented and tested. Changing provider accounts while jobs are pending is not a supported rotation.

## Delivery and verification

PostgreSQL tests cover service precedence, options refresh, client replacement, unchanged startup values, stale-value avoidance, rejected batches and failed status without credential disclosure. A fake HTTP transport verifies that the same AssemblyAI service sends its replacement token on the next request. Existing authorization checks include the reload route. No real provider credentials are used in tests. See [operations](../runbooks/integration-settings.md).
