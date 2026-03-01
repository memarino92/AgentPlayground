# PersonalAgent API

`PersonalAgent` is a minimal API wrapper around a Microsoft Agent Framework conversational agent. It exposes session-based chat endpoints so clients can create conversations, send messages, and retrieve per-session history.

## API Overview

- **Base behavior**: stateless HTTP endpoints with stateful in-memory conversation sessions.
- **Agent engine**: `Microsoft.Agents.AI` with OpenAI chat model (`gpt-4o-mini`).
- **Conversation memory**: maintained by `AgentSession` objects keyed by a generated `sessionId`.
- **Transcript storage**: mirrored in-memory as user/assistant message pairs.
- **Endpoint prefix**: agent operations are exposed under `/api`.

## Session Model

A **session** represents one conversation thread.

- Created via `POST /api/sessions`
- Identified by a GUID `sessionId`
- Backed by:
  - Agent framework conversation state (`AgentSession`)
  - Local transcript list (`ConversationMessage[]`)
- Used to preserve context across multiple requests in the same thread

If a session ID is unknown, endpoints return `404 Not Found`.

## Endpoints

### `GET /`

Health/status endpoint.

**Response**

```json
{
  "status": "healthy",
  "service": "PersonalAgent API"
}
```

### `POST /api/sessions`

Creates a new conversation session.

**Response**

```json
{
  "sessionId": "9f45af6c-5cde-40ef-a2e6-3e1143f95cb0",
  "message": "Session created successfully"
}
```

### `POST /api/sessions/{sessionId}/messages`

Sends a user message to the agent in an existing session.

**Request Body**

```json
{
  "message": "What do you remember about me?"
}
```

**Success Response**

```json
{
  "sessionId": "9f45af6c-5cde-40ef-a2e6-3e1143f95cb0",
  "response": "You said your name is Mike and you enjoy biking."
}
```

**Not Found Response**

```json
{
  "error": "Session not found"
}
```

### `GET /api/sessions/{sessionId}/messages`

Returns stored transcript for a session.

**Success Response**

```json
{
  "sessionId": "9f45af6c-5cde-40ef-a2e6-3e1143f95cb0",
  "messages": [
    { "role": "user", "content": "My name is Mike and I love riding my bike." },
    { "role": "assistant", "content": "Nice to meet you, Mike..." }
  ]
}
```

## Request Lifecycle

1. API receives a message request for a `sessionId`.
2. `AgentService` looks up the corresponding `AgentSession`.
3. User input is appended to in-memory transcript.
4. Agent runs with `RunAsync(message, session)`.
5. Agent response is converted to text and appended to transcript.
6. API returns the response payload.

## Security Behavior

- **Optional internal API key**: if `Security:InternalApiKey` is set, `/api/*` endpoints require `X-Internal-Api-Key` header.
- **CORS allowlist**: when `Security:AllowedOrigins` contains values, `/api/*` only allows those origins.
- **Rate limiting**: fixed-window limiter is applied to `/api/*` using `Security:RateLimit` settings.
- **Forwarded headers + HTTPS redirection**: enabled for reverse-proxy deployments.
- **Input guardrail**: message payloads must be non-empty and at most 4000 characters.

### Security Configuration Keys

```json
{
  "Security": {
    "AllowedOrigins": ["https://your-frontend-domain.example"],
    "InternalApiKey": "set-a-long-random-value",
    "RateLimit": {
      "PermitLimit": 60,
      "WindowSeconds": 60
    }
  }
}
```

## Internal Components

- **`AgentService`**
  - Initializes the `AIAgent` once at startup
  - Manages active sessions and transcripts
  - Handles message dispatch to the model
- **`MessageRequest`**
  - Input DTO for `/messages` endpoint
- **`ConversationMessage`**
  - Output/history record with `role` and `content`

## Configuration

### Overview

Configuration is managed via `IOptions<T>` pattern with dependency injection. All sensitive credentials (API keys) resolve in a predictable order:

1. **Environment variables** (used in production)
2. **User secrets or config files** (used in development)
3. **Default/empty values** (fallback)

### API Key Setup

#### OpenAI API Key

**Development**: Store in user secrets (never commit to version control).

```bash
dotnet user-secrets set OpenApiKey "your-openai-key" --project PersonalAgent
```

**Production** (Railway/Docker): Set environment variable at container runtime.

```bash
OPENAI_API_KEY="your-openai-key" dotnet run --project PersonalAgent
```

The API reads and resolves the key using the `IOptions<ApiKeyOptions>` pattern:

- Checks `OPENAI_API_KEY` environment variable first
- Falls back to `OpenApiKey` from configuration (user secrets in dev)
- Throws `InvalidOperationException` if neither is set

#### Internal API Key (Optional)

If you want to protect the `/api/*` endpoints behind an internal API key:

**Development**: Store in user secrets.

```bash
dotnet user-secrets set Security:InternalApiKey "your-internal-key" --project PersonalAgent
```

**Production**: Set environment variable.

```bash
INTERNAL_API_KEY="your-internal-key"
```

When set, clients must include the `X-Internal-Api-Key` header in all `/api/*` requests.

## Docker / Container Deployment

A `Dockerfile` is provided for containerized deployments (Railway, Docker Compose, Kubernetes, etc.).

### Build the image locally:

```bash
cd <root-directory>
docker build -t personalagent:latest -f PersonalAgent/Dockerfile .
```

### Run the container:

```bash
docker run -e OPENAI_API_KEY="your-key" -p 5000:5000 personalagent:latest
```

### Railway deployment:

1. Push your repository to GitHub.
2. Connect the repo to Railway.
3. Set the `OPENAI_API_KEY` environment variable in the Railway dashboard.
4. Railway auto-detects the Dockerfile and deploys.

The container exposes port 5000 and runs in Production mode (`ASPNETCORE_ENVIRONMENT=Production`).

## Current Behavior and Constraints

- Session and transcript data are in-memory only (lost on process restart).
- Data is local to one app instance (not shared across replicas).
- No authentication or authorization is applied yet.
- No custom tool execution pipeline is wired yet (this API is the base scaffold for that).

## Next Extension Points

- Add auth and per-user session ownership.
- Persist sessions/history in durable storage.
- Add custom tools/integrations (calendar, tasks, home automation, etc.).
- Add streaming responses and richer response metadata.
