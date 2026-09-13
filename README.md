# Agentic Digital Garden

A personal application suite for turning work journals, conversations, and coach check-ins into searchable knowledge and useful follow-up actions. Built with C#/.NET, Microsoft Agent Framework, PostgreSQL/pgvector, MassTransit, Blazor, and an Android companion.

The repository name remains `AgentPlayground` to preserve existing project and deployment paths. The product has grown beyond its original experiments: it now includes persistent conversations, role-based tool access, encrypted database configuration, background processing, and coaching workflows.

## What works today

- Persistent chat with saved model changes, first-send drafts, URL navigation, semantic memory, and server-scoped tools.
- Private work-journal ingestion and retrieval with PostgreSQL vector search.
- Coach audio uploads, transcription, live processing updates, speaker attribution, and dated retrieval evidence with retained audio and playback-following transcripts.
- GitHub owner and Google coach sign-in, athlete assignments, and a tool-permission admin UI.
- Unified Settings with encrypted Sentry/OpenTelemetry revisions, live provider credential reload, and database-owned chat model policy.
- Authorized durable agent and notification jobs with an activity dashboard, pending cancellation, conservative recovery, push outcomes, and separate approval records.
- Synthetic retrieval regressions and a repeatable opt-in coaching model evaluation runner; broader held-out quality evaluation remains open.
- Android companion for notifications, approvals, and a WebView; still needs authentication and release polish.

## Architecture

| Project | Responsibility |
| --- | --- |
| `PersonalAgent` | Private API, agent execution, chat, retrieval, authorization, and model services |
| `PersonalAgent.Web` | Authenticated Blazor UI and trusted API client |
| `PersonalAgent.Worker` | Background transcription, journal sync, transcript processing, and scheduled tasks |
| `AgentPlayground.Contracts` | Shared message contracts and cross-service infrastructure configuration |
| `AgentPlayground.Integrations` | Encrypted runtime settings, Sentry reporting, OpenTelemetry and OpenInference instrumentation |
| `PersonalAgent.Mobile` | Android MAUI companion |

PostgreSQL stores application data, encrypted settings, vectors, and MassTransit SQL transport. Root `Dockerfile.personalagent-*` files define service builds; `infrastructure/backup/` contains the backup uploader.

The API owns AI provider adapters, including AssemblyAI transcription behind a neutral bus capability with persisted job tracking. Worker retains domain processing. Embedding-space compatibility and fully durable domain workflows remain planned work. See [architecture](docs/architecture.md) and the [decision register](docs/README.md).

## Build and test

Install .NET 11 RC1 SDK `11.0.100-rc.1.26425.128`, pinned in `global.json`. Backend projects can be built without Android workloads:

```powershell
dotnet build PersonalAgent/PersonalAgent.csproj
dotnet build PersonalAgent.Web/PersonalAgent.Web.csproj
dotnet build PersonalAgent.Worker/PersonalAgent.Worker.csproj
```

Run all four backend test projects. Integration tests use disposable Docker databases:

```powershell
dotnet test AgentPlayground.Contracts.Tests/AgentPlayground.Contracts.Tests.csproj
dotnet test PersonalAgent.Tests/PersonalAgent.Tests.csproj
dotnet test PersonalAgent.Web.Tests/PersonalAgent.Web.Tests.csproj
dotnet test PersonalAgent.Worker.Tests/PersonalAgent.Worker.Tests.csproj
```

`dotnet build` includes Android and requires the matching .NET 11 MAUI Android workload, Android SDK, and Java toolchain. Follow the [mobile setup](PersonalAgent.Mobile/README.md). Package versions are centrally managed in `Directory.Packages.props`; warnings are errors.

For an interactive demo without private data or provider accounts, start the synthetic stack with Docker:

```powershell
docker compose -f compose.synthetic.yml up --build --detach --wait --wait-timeout 180
```

Open [demo sign-in](http://127.0.0.1:15000/login). See the [synthetic walkthrough and smoke test](docs/runbooks/synthetic-demo.md). For real integrations, follow [local development](docs/runbooks/local-development.md).

## Direction and operating notes

- [Roadmap](docs/plans/roadmap.md): local parity/recovery, AI gateway, Android, and durable agent workflows.
- [Snapshot commands](docs/runbooks/snapshot-commands.md): export and restore locally with checksum and configuration-key verification.
- [Integration settings](docs/runbooks/integration-settings.md): administrator setup, validation, and reload; [observability](docs/runbooks/observability.md) covers live telemetry export.
- [Scheduled jobs](docs/runbooks/scheduled-jobs.md): authorization, cancellation, outcomes, and recovery limits.
- [Navigation and playback](docs/runbooks/navigation.md): URL state, chat preferences, and shared recording views.
- [Coaching evaluation](docs/runbooks/coach-retrieval-evaluation.md): regression baseline, model comparisons, and remaining quality evidence.
- [Recovery design and rehearsal](docs/runbooks/database-recovery.md): complete application recovery remains to be proven.
- [Public-release review](docs/runbooks/public-release.md): cleanup completed and remaining publication work.
- [Decision docs](docs/README.md): accepted choices, proposals, and how to record future decisions.
- [API details](PersonalAgent/README.md), [Web details](PersonalAgent.Web/README.md), and [contributor instructions](AGENTS.md).

Production runs on Railway. Each server service boots with `DATABASE_URL` and `CONFIG_ENCRYPTION_KEY` after configuration seeding. Keep the decryption key outside the database backup. Never commit populated seed files, database snapshots, personal journals, coaching audio, or credentials.

This is a personal system under active development. The synthetic demo exercises local application behavior; public availability and proven disaster recovery remain tracked deliverables. Licensed under the [MIT License](LICENSE).
