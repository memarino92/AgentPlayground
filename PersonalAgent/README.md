# PersonalAgent API

`PersonalAgent` is the central API service for chat orchestration and model tasks in the AgentPlayground stack.

## What This Service Owns

- Session-based chat APIs (`/api/sessions`, `/api/sessions/{id}/messages`, `/api/models`).
- Agent orchestration with Microsoft Agent Framework.
- Provider-bound chat, extraction, embeddings and persisted AssemblyAI transcription jobs behind app services.
- Coaching retrieval with dated utterance evidence, subject-authorized audio playback and retention controls.
- Durable agent/notification job execution with current authorization and conservative recovery.
- Administrator settings revisions, live API credentials and database-owned model policy.
- Work journal model tasks consumed over MassTransit request/response:
  - parse markdown into journal entries
  - generate embeddings for entry content
- Querying `work_journal_entries` via pgvector for RAG tool responses.

The Worker is now orchestration-only for journal sync and calls these model tasks via MassTransit contracts rather than calling provider SDKs directly.

## API Endpoints

- `GET /` health/status.
- `GET /api/models` available chat models from a cached provider inventory and API-owned policy; see [runtime model behavior](../docs/runbooks/runtime-chat-models.md).
- `POST /api/sessions` create a session.
- `GET /api/sessions` list sessions (cursor paging).
- `PUT /api/sessions/{sessionId}/model` persist an authorized model change from the current catalog.
- `POST /api/schedule/agent-tasks` and `/api/schedule/notifications` schedule authorized jobs.
- `GET /api/jobs`, `GET /api/jobs/{id}` and `POST /api/jobs/{id}/cancel` inspect or cancel permitted pending work; see [job operations](../docs/runbooks/scheduled-jobs.md).
- `POST /api/sessions/{sessionId}/messages` send message.
- `GET /api/sessions/{sessionId}/messages` read transcript.
- `POST /api/mobile/devices/register` register/update mobile push token.
- `POST /api/approvals` create a pending approval and trigger push delivery.
- `POST /api/approvals/{approvalId}/decision` approve/deny a pending request.
- `GET /api/approvals/{approvalId}` read approval status.

`/api/*` endpoints use the internal API key filter when configured. Chat, scheduling, job inspection and evidence routes also enforce signed actor and subject access; administrator settings have an explicit administrator policy. Mobile registration and approval routes still need the identity hardening tracked in the roadmap. The list above is a core-route overview, not a complete endpoint reference.

## Messaging Role

This service uses MassTransit SQL transport and handles:

- Work journal request/response consumers:
  - `ParseWorkJournalEntriesRequest`
  - `GenerateEmbeddingsRequest`

These consumers are registered with retry policies and respond with typed contracts from `AgentPlayground.Contracts`.

## Required Configuration

Production uses PostgreSQL-backed configuration. Seed it once with
`scripts/seed-configuration.ps1`, then configure only these bootstrap variables:

```text
DATABASE_URL
CONFIG_ENCRYPTION_KEY
```

`CONFIG_ENCRYPTION_KEY` must be the same base64-encoded 32-byte key used by the seed script.
Application settings and encrypted secrets are loaded from `app.configuration_settings`.
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

These legacy environment bindings remain available when database-backed configuration
is disabled locally, but are not needed after the database has been seeded.

For database-backed push configuration, enable `PushNotifications:Enabled` and configure `PushNotifications:ServiceAccountJson`, `PushNotifications:ServiceAccountJsonBase64`, or `PushNotifications:ServiceAccountPath`. The environment aliases above are legacy alternatives. New application settings must be database-first; bootstrap secrets remain external.

## Local Run

Use the shared [local development runbook](../docs/runbooks/local-development.md) for current database-backed configuration, required credentials, startup order, and OAuth callback URLs.

## Notes

- Session/transcript state is persisted to PostgreSQL through the session store service.
- Work-journal parse/embedding provider logic is intentionally centralized here so provider swaps do not affect worker behavior.
- If `INTERNAL_API_KEY` is set, clients must send `X-Internal-Api-Key`.
