# AgentPlayground

An exploration workspace for creating agents with the **Microsoft Agent Framework** (currently in RC for v1.0). This solution serves as an experimental playground for understanding agent capabilities and patterns using modern C# practices.

## Overview

AgentPlayground is a collection of projects designed to explore different aspects of the Microsoft Agent Framework. Projects may be related, unrelated, or experimental in nature—this is a space for learning and discovery.

## Requirements

- **.NET**: Latest stable releases and pre-release versions (currently targeting .NET 10.0+)
- **C# Language Features**: Modern language features are encouraged
- **API Keys**: Projects may require API keys (e.g., OpenAI) configured via user secrets

## Project Structure

```
AgentPlayground/
├── AgentPlayground.slnx                   # Solution file
├── .github/
│   └── copilot-instructions.md            # Copilot guidelines for this workspace
├── README.md                              # This file
├── MyFirstAgent/                          # Simple agent getting started example
│   ├── MyFirstAgent.csproj
│   ├── Program.cs
│   └── ...
├── PersonalAgent/                         # Minimal API personal assistant agent
│   ├── PersonalAgent.csproj
│   ├── Program.cs
│   ├── README.md
│   └── ...
├── PersonalAgent.Web/                     # Blazor Server frontend
│   ├── PersonalAgent.Web.csproj
│   ├── Components/
│   │   └── Pages/Chat.razor
│   └── ...
├── AgentPlayground.Contracts/             # Shared message contracts and messaging options
│   ├── AgentPlayground.Contracts.csproj
│   └── ...
├── PersonalAgent.Worker/                  # Background worker consuming MassTransit events
│   ├── PersonalAgent.Worker.csproj
│   ├── Program.cs
│   └── ...
├── scripts/                              # Local infrastructure helper scripts
│   ├── start-postgres.ps1
│   └── stop-postgres.ps1
└── [Additional projects]/
```

## Current Projects

### MyFirstAgent

A beginner-friendly example demonstrating:

- Creating an agent with the Microsoft Agent Framework
- Using OpenAI's GPT-4o-mini model
- Managing conversation sessions and history
- Following modern C# style guidelines

**Technology**: .NET 10.0, Microsoft.Agents.AI (RC 1.0), OpenAI

### PersonalAgent

A complete multi-service personal assistant system demonstrating:

- **PersonalAgent API**: Session-based chat endpoints in ASP.NET Core Minimal APIs
- **PersonalAgent.Web**: Blazor Server frontend for interactive chat
- **PersonalAgent.Worker**: Hosted background worker consuming and logging test events
- **AgentPlayground.Contracts**: Shared event contracts and messaging configuration

**Key Features**:

- Microsoft Agent Framework integration with OpenAI (gpt-4o-mini)
- MassTransit integration using PostgreSQL SQL transport
- Shared Postgres-backed event bus between web, agent, and worker
- Agent tool calling to publish follow-up bus messages
- Security hardening: CORS, rate limiting, internal API key validation
- IOptions<T> pattern for clean dependency injection
- Environment variable support for production deployment

**Technology**: .NET 10.0, ASP.NET Core Minimal APIs, Blazor Server, Worker Services, Microsoft.Agents.AI (RC 1.0), OpenAI, MassTransit 8.5, PostgreSQL

**Running locally**:

```powershell
pwsh -NoProfile -File .\scripts\start-postgres.ps1
dotnet run --project PersonalAgent
dotnet run --project PersonalAgent.Worker
dotnet run --project PersonalAgent.Web
```

This starts the shared PostgreSQL transport plus the API, worker, and Blazor frontend.

### Startup Steps

1. Ensure Docker Desktop is running so [`start-postgres.ps1`](/C:/Users/Michael/projects/AgentPlayground/scripts/start-postgres.ps1) can start the shared PostgreSQL container.
2. Configure your OpenAI key for [`PersonalAgent`](/C:/Users/Michael/projects/AgentPlayground/PersonalAgent/PersonalAgent.csproj):

```powershell
dotnet user-secrets set "OpenApiKey" "your-openai-api-key" --project PersonalAgent
```

3. Start PostgreSQL:

```powershell
pwsh -NoProfile -File .\scripts\start-postgres.ps1
```

4. Start the agent API:

```powershell
dotnet run --project PersonalAgent
```

The agent requires both `OpenApiKey` and `Messaging:ConnectionString` in user secrets.

5. Start the worker:

```powershell
dotnet run --project PersonalAgent.Worker
```

The worker requires `Messaging:ConnectionString` in user secrets.

6. Start the web app:

```powershell
dotnet run --project PersonalAgent.Web
```

The web app requires `Messaging:ConnectionString` in user secrets.

7. Open the web app, sign in, click `Fire Test Event`, and verify the worker logs:
   - the initial `TestEventRequested`
   - the follow-up `AgentGeneratedTestMessage`

8. Stop PostgreSQL when finished:

```powershell
pwsh -NoProfile -File .\scripts\stop-postgres.ps1
```

### Messaging Secrets

The shared PostgreSQL transport connection string is intentionally not stored in `appsettings.json`. Set it in user secrets for all three projects:

```powershell
dotnet user-secrets set "Messaging:ConnectionString" "Host=localhost;Port=5432;Database=agentplayground;Username=agentplayground;Password=agentplayground" --project .\PersonalAgent
dotnet user-secrets set "Messaging:ConnectionString" "Host=localhost;Port=5432;Database=agentplayground;Username=agentplayground;Password=agentplayground" --project .\PersonalAgent.Web
dotnet user-secrets set "Messaging:ConnectionString" "Host=localhost;Port=5432;Database=agentplayground;Username=agentplayground;Password=agentplayground" --project .\PersonalAgent.Worker
```

For cloud deployments, the apps also accept `MESSAGING_CONNECTION_STRING`. If Railway gives you a PostgreSQL URL such as `postgresql://...`, the apps normalize that URL into the Npgsql-style connection string expected by MassTransit.

### Messaging Gotchas

- MassTransit SQL transport for PostgreSQL needs `AddPostgresMigrationHostedService(...)` to create the schema and transport infrastructure. Creating the schema manually is not enough.
- `AddPostgresMigrationHostedService(...)` does not use custom app config objects automatically. `SqlTransportOptions.ConnectionString` must be bound explicitly from `Messaging:ConnectionString`.
- PostgreSQL SQL transport uses explicit topic subscriptions. Published events that should fan out to multiple consumers need SQL endpoint subscriptions, not just `IConsumer<T>` registrations.
- If you change SQL transport topology or subscriptions, recreate the Postgres container to avoid stale infrastructure state during local development.
- `IPublishEndpoint` is scoped. Long-lived agent services that hold in-memory session state should depend on `IBus` instead of capturing scoped `IPublishEndpoint`.

## Railway Deployment

Deploy this solution as four Railway services: one PostgreSQL service plus three app services.

### One-Command Bootstrap

If you want to minimize dashboard work, use the included bootstrap script. It creates or reuses the Railway project, creates the three app services, configures their Dockerfile paths and watch patterns, provisions a Railway domain for the web app, wires shared variables, and points the web app at the API over Railway private networking.

Copy [.env.railway.example](/C:/Users/Michael/projects/AgentPlayground/.env.railway.example) to `.env.railway`, fill in the values, then run:

```powershell
Copy-Item .env.railway.example .env.railway
pwsh -NoProfile -File .\scripts\set-internal-api-key.ps1
pwsh -NoProfile -File .\scripts\check-railway-env.ps1
pwsh -NoProfile -File .\scripts\setup-railway.ps1 -CreatePostgres
```

The script reads `.env.railway` automatically. You can still override anything with explicit script parameters or process-level environment variables.

Deployment secrets can also be sourced from local user secrets automatically. The Railway scripts currently fall back to:

- `PersonalAgent` user secrets for `OpenApiKey` and `Security:InternalApiKey`
- `PersonalAgent.Web` user secrets for `Authentication:Schemes:GitHub:ClientId`, `ClientSecret`, `AllowedUsers`, and `CallbackPath`

If you have not created an internal API key yet, run [`set-internal-api-key.ps1`](/C:/Users/Michael/projects/AgentPlayground/scripts/set-internal-api-key.ps1). It generates a strong key, stores it in `PersonalAgent` user secrets, and writes the same value to `.env.railway`.

What still remains after that:

- If `-CreatePostgres` cannot provision PostgreSQL automatically with your installed Railway CLI, create one Railway PostgreSQL service in the same project/environment, name it `Postgres`, and rerun the setup script
- Ensure Railway has access to the GitHub repository if you use `-RepoSlug`
- Create or update your GitHub OAuth app to use the printed callback URL
- Push to the configured branch, or use [`deploy-railway.ps1`](/C:/Users/Michael/projects/AgentPlayground/scripts/deploy-railway.ps1) if you created empty services instead of repo-backed ones

To redeploy later with the same local file:

```powershell
pwsh -NoProfile -File .\scripts\check-railway-env.ps1
pwsh -NoProfile -File .\scripts\deploy-railway.ps1
```

### Service Layout

1. `personalagent-api`
   - Dockerfile path: `Dockerfile.personalagent-api`
   - Private-only service by default
2. `personalagent-web`
   - Dockerfile path: `Dockerfile.personalagent-web`
3. `personalagent-worker`
    - Dockerfile path: `Dockerfile.personalagent-worker`
4. `postgres`
   - Use Railway PostgreSQL

### Shared Variables

Set these on all three app services:

```text
MESSAGING_CONNECTION_STRING=${{Postgres.DATABASE_URL}}
MESSAGING_SCHEMA=transport
```

### API Variables

Set these on `personalagent-api`:

```text
OPENAI_API_KEY=...
ALLOWED_ORIGINS=https://your-web-service.up.railway.app
INTERNAL_API_KEY=generate-a-long-random-value
```

`PORT` is provided automatically by Railway and the API now binds to it without extra setup.

### Web Variables

Set these on `personalagent-web`:

```text
GITHUB_CLIENT_ID=...
GITHUB_CLIENT_SECRET=...
GITHUB_ALLOWED_USERS=your-github-login
PERSONAL_AGENT_API_BASE_URL=http://${{personalagent-api.RAILWAY_PRIVATE_DOMAIN}}:${{personalagent-api.PORT}}
PERSONAL_AGENT_INTERNAL_API_KEY=same-value-as-api-internal-key
```

GitHub OAuth should use these URLs:

```text
Homepage URL: https://your-web-service.up.railway.app
Authorization callback URL: https://your-web-service.up.railway.app/signin-github
```

The web app now trusts forwarded host/protocol headers so GitHub callback URLs are generated correctly behind Railway's proxy. It also talks to the API over Railway private networking, so the API does not need a public domain.

### Worker Variables

`personalagent-worker` only needs the shared messaging variables.

### Notes

- Keep each app as a single instance unless you replace the API's in-memory session storage.
- The web app and API both bind Railway's `PORT` automatically.
- If you enable `INTERNAL_API_KEY` on the API, set the same value as `PERSONAL_AGENT_INTERNAL_API_KEY` on the web service.

### Agent Event Tool Pattern

When adding a new agent-driven event flow, use this pattern:

1. Add the event contract to [`AgentPlayground.Contracts`](/C:/Users/Michael/projects/AgentPlayground/AgentPlayground.Contracts/AgentPlayground.Contracts.csproj).
2. Add or update the consuming MassTransit consumer in [`PersonalAgent`](/C:/Users/Michael/projects/AgentPlayground/PersonalAgent/Program.cs).
3. If the event is published and should reach the agent queue, add an explicit SQL endpoint subscription with `AddSqlConfigureEndpointCallback(... cfg.Subscribe<T>(...))`.
4. Expose a tool function from [`AgentService`](/C:/Users/Michael/projects/AgentPlayground/PersonalAgent/Services/AgentService.cs) using `AIFunctionFactory.Create(...)`.
5. Publish the follow-up event from that tool using `IBus.Publish(...)`.
6. In the consumer, prompt the agent to call the tool exactly once for deterministic event publishing.

## Getting Started

### 1. Prerequisites

Ensure you have .NET 10.0 or later installed:

```bash
dotnet --version
```

### 2. Clone or Navigate to the Workspace

```bash
cd AgentPlayground
```

### 3. Restore and Build

```bash
dotnet build
```

### 4. Configure Secrets (if needed)

For projects that require API keys (like MyFirstAgent with OpenAI):

```bash
dotnet user-secrets init --project MyFirstAgent
dotnet user-secrets set "OpenApiKey" "your-api-key-here" --project MyFirstAgent
```

For PersonalAgent:

```bash
dotnet user-secrets set "OpenApiKey" "your-api-key-here" --project PersonalAgent
```

### 5. Run a Project

**MyFirstAgent** (standalone console app):

```bash
dotnet run --project MyFirstAgent
```

**PersonalAgent** (API only):

```bash
dotnet run --project PersonalAgent
```

**PersonalAgent multi-project flow** (API + worker + Blazor frontend):

```powershell
pwsh -NoProfile -File .\scripts\start-postgres.ps1
dotnet run --project PersonalAgent
dotnet run --project PersonalAgent.Worker
dotnet run --project PersonalAgent.Web
```

Open the web app, click `Fire Test Event`, and verify the worker logs both the initial test event and the agent-generated follow-up event.

## Development Guidelines

### Code Style

This workspace emphasizes modern, expressive C# code:

- **Expression-bodied members** for concise logic
- **One-line if statements** without braces for simple conditions
- **Early returns** to minimize nesting
- **Switch expressions** instead of traditional switch statements
- **Collection expressions** for cleaner initialization
- **Visually appealing, functional-informed style** over procedural patterns

For detailed examples and guidelines, see [.github/copilot-instructions.md](.github/copilot-instructions.md).

### Adding New Projects

When adding a new project to the solution:

1. Create a new project directory
2. Create a `.csproj` file targeting the appropriate .NET version
3. Update `.github/copilot-instructions.md` to document:
   - The new project's purpose
   - Any new dependencies or frameworks
   - Changes to build/run instructions
4. Update this `README.md` with:
   - Project description under "Current Projects"
   - Setup or configuration instructions specific to the project
   - Links to relevant documentation

## Building and Running

### Build the entire solution:

```bash
dotnet build
```

### Build a specific project:

```bash
dotnet build --project <ProjectPath>
```

### Run a specific project:

```bash
dotnet run --project <ProjectPath>
```

### Clean build artifacts:

```bash
dotnet clean
```

## Resources

- [Microsoft Agent Framework Documentation](https://learn.microsoft.com/en-us/azure/ai-services/agents/)
- [.NET 10.0 Documentation](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10)
- [C# Language Features](https://learn.microsoft.com/en-us/dotnet/csharp/)
- [OpenAI API Documentation](https://platform.openai.com/docs)

## Notes

- This is an **experimental/exploration workspace**—code here may be unstable or incomplete
- Embrace latest C# language features and .NET versions
- Prioritize code clarity and expressiveness
- Refer to `.github/copilot-instructions.md` for workspace-specific guidance
- When making significant changes, update documentation files accordingly

## License

[Add your license information here]
