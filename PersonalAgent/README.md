# PersonalAgent API

`PersonalAgent` is the central API service for chat orchestration and model tasks in the AgentPlayground stack.

## What This Service Owns

- Session-based chat APIs (`/api/sessions`, `/api/sessions/{id}/messages`, `/api/models`).
- Agent orchestration with Microsoft Agent Framework.
- Provider-bound model work (OpenAI) behind app services.
- Work journal model tasks consumed over MassTransit request/response:
  - parse markdown into journal entries
  - generate embeddings for entry content
- Querying `work_journal_entries` via pgvector for RAG tool responses.

The Worker is now orchestration-only for journal sync and calls these model tasks via MassTransit contracts rather than calling provider SDKs directly.

## API Endpoints

- `GET /` health/status.
- `GET /api/models` available chat models.
- `POST /api/sessions` create a session.
- `GET /api/sessions` list sessions (cursor paging).
- `POST /api/sessions/{sessionId}/messages` send message.
- `GET /api/sessions/{sessionId}/messages` read transcript.
- `POST /api/mobile/devices/register` register/update mobile push token.
- `POST /api/approvals` create a pending approval and trigger push delivery.
- `POST /api/approvals/{approvalId}/decision` approve/deny a pending request.
- `GET /api/approvals/{approvalId}` read approval status.

`/api/*` endpoints use the internal API key filter when configured.

## Messaging Role

This service uses MassTransit SQL transport and handles:

- Event consumption (`TestEventRequested`).
- Work journal request/response consumers:
  - `ParseWorkJournalEntriesRequest`
  - `GenerateEmbeddingsRequest`

These consumers are registered with retry policies and respond with typed contracts from `AgentPlayground.Contracts`.

## Required Configuration

Use either environment variables or user secrets.

```text
OPENAI_API_KEY               -> OpenAI:ApiKey
MESSAGING_CONNECTION_STRING  -> Messaging:ConnectionString
```

Startup validation is enabled for required options, including `OpenAI:ApiKey`.

Optional:

```text
INTERNAL_API_KEY             -> Security:InternalApiKey
ALLOWED_ORIGINS              -> Security:AllowedOrigins
MESSAGING_SCHEMA             -> Messaging:Schema
PUSH_NOTIFICATIONS_ENABLED   -> PushNotifications:Enabled
FIREBASE_PROJECT_ID          -> PushNotifications:FirebaseProjectId
FIREBASE_SERVICE_ACCOUNT_JSON -> PushNotifications:ServiceAccountJson
FIREBASE_SERVICE_ACCOUNT_JSON_BASE64 -> PushNotifications:ServiceAccountJsonBase64
FIREBASE_SERVICE_ACCOUNT_PATH -> PushNotifications:ServiceAccountPath
ANDROID_PUSH_CHANNEL_ID      -> PushNotifications:AndroidChannelId
```

For push notifications, set `PUSH_NOTIFICATIONS_ENABLED=true` and provide either
`FIREBASE_SERVICE_ACCOUNT_JSON`, `FIREBASE_SERVICE_ACCOUNT_JSON_BASE64`, or `FIREBASE_SERVICE_ACCOUNT_PATH`.

## Local Run

```powershell
pwsh -NoProfile -File .\scripts\start-postgres.ps1
dotnet user-secrets set "OpenAI:ApiKey" "your-openai-key" --project .\PersonalAgent
dotnet user-secrets set "Messaging:ConnectionString" "Host=localhost;Port=5432;Database=agentplayground;Username=agentplayground;Password=agentplayground" --project .\PersonalAgent
dotnet run --project .\PersonalAgent
```

## Notes

- Session/transcript state is persisted to PostgreSQL through the session store service.
- Work-journal parse/embedding provider logic is intentionally centralized here so provider swaps do not affect worker behavior.
- If `INTERNAL_API_KEY` is set, clients must send `X-Internal-Api-Key`.
