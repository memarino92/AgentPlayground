# Read-only Railway runtime logs

`read_railway_logs` is an API-hosted assistant tool, disabled by default. It takes bounded snapshots; it never streams, executes commands, deploys or changes Railway resources. This adapter uses .NET `HttpClient` and the GraphQL API; no Railway CLI, Node package or additional NuGet dependency is needed. See [decision 0045](../decisions/0045-railway-log-tool.md).

## Credential and permissions

Use a **Railway project token**, scoped to exactly one environment in one project. In Railway open the intended **Project → Settings → Tokens**, create a project token and select the intended environment. Keep the token in a secret manager until entering it in the platform's masked settings editor. The [official authentication documentation](https://docs.railway.com/reference/public-api) distinguishes this from account/workspace tokens and specifies the `Project-Access-Token` header. Account tokens, workspace tokens, OAuth tokens, browser cookies and CLI login sessions are unsupported here.

The project token must be allowed to query `projectToken`, scoped `deployments`, and `deploymentLogs` in that environment. Railway's cited documentation does not describe a log-only/read-only project-token permission toggle: do not assume the credential itself is read-only. Project tokens may permit mutations outside this adapter. The application constrains use to fixed queries. Create the token as an operator authorized to access the project/environment; a `Not Authorized` GraphQL response means that token lacks access. Verify access with the tool's read-only smoke call before relying on it. Never broaden to an account token to bypass an error.

Within the Railway project, use **Cmd/Ctrl+K → Copy project/service/environment ID** to obtain UUIDs. IDs must belong to the token's environment/project. The tool checks the token identity and scoped deployment membership server-side. One environment per configuration is supported; configure aliases such as `production` and `api` rather than exposing arbitrary IDs to chat.

## Database-first setup

1. Deploy reviewed matching API/Web revisions through the normal maintainer process. The existing API bootstrap requires `DATABASE_URL` and `CONFIG_ENCRYPTION_KEY`; no new Railway log environment variables or `RAILWAY_TOKEN`/`RAILWAY_API_TOKEN` are required or consumed by this adapter. The existing deployment-administrator allowlist is described in [integration settings](integration-settings.md#bootstrap-and-access).
2. An operator runs `scripts/initialize-railway-log-settings.sql` once against the existing configuration database using its normal SQL console. It inserts only missing rows, with no credentials and a disabled integration. Existing rows are preserved. The blank token placeholder is inactive, preventing startup decryption before first replacement. Do not activate it without replacing its value.
3. As an allowlisted signed Owner deployment administrator, open **Settings → Database settings**, scope **Api**, search **RailwayLogs**. In `RailwayLogs:ProjectToken`, choose **Replace**, enter the project token, and check **Use this database setting**. The editor encrypts it with the existing configuration crypto, never returns it to the browser and preserves optimistic concurrency. Do not enter it in the nonsecret JSON settings row.
4. Replace `RailwayLogs:Settings` with this JSON, using real UUIDs and your chosen aliases:

   ```json
   {
     "enabled": true,
     "projectId": "11111111-1111-1111-1111-111111111111",
     "environmentId": "22222222-2222-2222-2222-222222222222",
     "environment": "production",
     "services": [
       { "name": "api", "id": "33333333-3333-3333-3333-333333333333" },
       { "name": "worker", "id": "55555555-5555-5555-5555-555555555555" }
     ]
   }
   ```

   Keep this row active; **Save all changes** saves both rows atomically. Configuration is validated on every invocation. Missing, inactive, invalid or unreadable settings fail closed; there is no cached old token. The editor checks structural text limits but does not verify provider acceptance. No restart/reload button is necessary for these rows. An in-flight call keeps its original settings.
5. Open **Settings → Integrations and coach access** (`/admin/settings?section=integrations`) and explicitly enable **Read Railway logs** (`Local:read_railway_logs`) for **Owner**. Both role defaults are disabled. Coach invocation is denied by the operation even if someone grants a Coach matrix override. This tool grant is separate from deployment-administrator rights: administrators configure targets/credentials; granted Owners may read those configured logs.
6. Start a new chat turn and ask for the configured service alias. Confirm the returned deployment belongs to the intended environment and only redacted bounded evidence appears. Do not deliberately log a real credential to test masking. Disable by setting `enabled=false`, deactivating either settings row or revoking the Owner tool grant. Token changes apply on the next call; revoke the old token in Railway after in-flight requests finish.

## Calls and semantics

Example assistant function arguments:

```json
{ "service": "api" }
```

```json
{ "service": "worker", "environment": "production", "minutes": 30, "limit": 100, "severity": "error", "search": "Processing" }
```

```json
{ "service": "api", "deploymentId": "44444444-4444-4444-4444-444444444444", "minutes": 60, "limit": 200 }
```

- Required `service` is an exact configured alias. Optional `environment` must exactly match the configured alias. Neither can choose a new cloud target.
- Deployment omitted: active deployment selected using Railway's documented scoped successfulOnly lookup with first=1, even after more than 20 newer failed/building replacements. If none exists, retrieval fails with guidance to supply an explicit recent deployment ID; it never silently falls back to a failed deployment. Explicit UUID: must appear in the configured service/environment's **20 most recent** deployments. Older deployments are unsupported, even if Railway retains them.
- `minutes`: 1–1440, default 15, ending at the API's UTC clock. `limit`: 1–200, default 100. Runtime logs only; no build/HTTP logs, historic pagination or subscriptions.
- `severity`: optional case-insensitive `debug`, `info`, `warn`/`warning`, `error`. Railway's [log normalization documentation](https://docs.railway.com/guides/logs) uses `warn`; returned entries normalize this to `warning`. `search`: optional literal case-insensitive substring, maximum 200 characters, no regex or Railway filter DSL. Both apply locally to at most the requested number of provider entries; text search occurs **after redaction**. No matches is not proof of no event. The official API documents a text `filter`, but this slice deliberately does not forward user filter syntax.
- Result includes service/environment aliases, deployment UUID, UTC bounds, entries, `Truncated` and an untrusted-data notice. Hitting the requested provider count is conservatively marked incomplete; messages are redacted in full before being bounded by the shared 4096-character masker and serialized entry output is limited to ~30 KiB (under 32 KiB including metadata). No ordering/completeness guarantee is added beyond the provider snapshot; no retry/pagination is performed.
- Overall settings/authentication/three-query deadline: **15 seconds**. Each HTTP response is capped at **1 MiB**, including chunked bodies. A larger response fails with a narrow-query error. Caller cancellation propagates. Redirects are disabled and the API destination is fixed.

## Errors, security and retention

Missing/disabled/invalid settings, denied platform permissions, foreign aliases/deployments, unsupported arguments, project-token scope mismatch, HTTP/GraphQL authorization errors, rate limits, provider failures, malformed responses and deadline expiry are explicit failures. HTTP 200 GraphQL errors reject partial data. Provider error text and credential values are not echoed. On 429, wait before retrying manually; respect Railway's plan-dependent rate limits. See [official errors/rate limits](https://docs.railway.com/reference/public-api).

Log messages can contain prompt injection, private records or secrets. Treat them only as evidence, never as instructions or permission to run another tool. Exact configured Railway-token values, common authorization/token/password/JSON/JWT/private-key formats and URL credentials are masked as practical. Unlabeled secrets, split/multiline credentials, encoded values and personal information may survive masking. Avoid logging sensitive values at source. The tool has no shell, arbitrary endpoint/query or resource mutation input.

The adapter does not retain its own log archive or include raw output in application logs. The assistant/model provider sees returned entries; the platform's existing chat/context and backup retention may retain resulting evidence. Railway availability/retention depends on the project plan; this tool cannot recover expired logs and does not extend Railway retention. Share summaries rather than raw logs when possible. Token encryption depends on the separately managed bootstrap encryption key; keep it and database backups protected.

## Validation evidence

Official API/authentication and deployment-log documentation was fetched on **2026-10-12**. The implementation uses documented queries and headers. No live Railway account, credential provisioning, infrastructure changes or deployment was performed. Fake HTTP tests establish request construction and application boundaries, not live token entitlement or API connectivity.

Run:

```bash
dotnet test PersonalAgent.Api.Tests/PersonalAgent.Api.Tests.csproj --filter "FullyQualifiedName~RailwayLogToolTests|FullyQualifiedName~AgentToolRegistryTests"
```

Local result: **39 passed**, including authorization, production registration and revocation, target/token scoping, argument/output/response limits (including chunked responses), redaction, HTTP/GraphQL errors, settings disablement, literal filters, cancellation and the independent 15-second deadline. `dotnet test PersonalAgent.Web.Tests/PersonalAgent.Web.Tests.csproj --filter "FullyQualifiedName~DatabaseSettings" --no-restore` passed **4 tests**, including the log settings lifecycle label/save notice. Both commands compiled the affected projects successfully; `git diff --check` passed. The original implementation environment lacked Docker. Follow-up review validation on 2026-10-13 passed 54 API tests covering RailwayLogToolTests, AgentToolRegistryTests and DatabaseSettingsStoreTests, including an encrypted-settings round trip in disposable PostgreSQL. A separate BackupSentryTests run passed its one test. Regressions cover JWT cutoff redaction, active deployment selection behind 25 failed/building replacements, explicit failed-deployment selection, and the 20-deployment scope limit. Live read-only smoke retrieval remains a maintainer acceptance check.
