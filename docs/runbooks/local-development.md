# Local development

For a fresh checkout without private data or provider accounts, use the [synthetic demo](synthetic-demo.md). The instructions below cover manual development with real integrations. Do not start application services against an unsanitized production restore; follow [database recovery](database-recovery.md) first.

## Build without production data

The .NET 11 RC1 SDK pinned in `global.json` builds the server projects independently of MAUI. Run the four test projects listed in the root README; integration tests require Docker Desktop. A complete solution build also needs Android tooling. Use the [mobile setup](../../PersonalAgent.Mobile/README.md) for the matching workload and platform tooling; installing the base SDK alone does not install MAUI Android.

## Preferred path: exercise database-backed configuration locally

1. Start Docker Desktop and a fresh local PostgreSQL instance:

   ```powershell
   powershell.exe -NoProfile -File scripts/start-postgres.ps1
   ```

   The script defaults to pgvector/PostgreSQL 18 on port 5432. It starts an existing named container without changing its image or data. Compose uses the same container name; choose one infrastructure owner and avoid running both approaches at once.

2. Copy the configuration template:

   ```powershell
   Copy-Item scripts/seed-configuration.values.ps1.example scripts/seed-configuration.values.ps1
   ```

   Fill it with development values. Use a new local configuration encryption key and local internal/signing keys. Set the API URL to `http://localhost:5100`, allowed Web origin to `https://localhost:5011`, and development OAuth clients/allowlists. Provide required model/transcription credentials if those services will be exercised. Keep push disabled and optional private journal credentials empty. This manual seed validates integration credentials; the separate [synthetic demo](synthetic-demo.md) initializes its own configuration without them.

3. Generate and apply the seed to the local database only. With `-Apply`, the values file's `$DatabaseUrl` must be a PostgreSQL URL reachable from the seed script's Docker client; use `host.docker.internal` for a database port exposed by the host. For example, the default local instance uses `postgresql://agentplayground:agentplayground@host.docker.internal:5432/agentplayground`.

   ```powershell
   powershell.exe -NoProfile -File scripts/seed-configuration.ps1 -Apply
   ```

   Alternatively, generate without `-Apply` and execute the generated SQL in a local database client. Never apply a local seed to the production database.

4. Set `DATABASE_URL` to the local database URL and `CONFIG_ENCRYPTION_KEY` to the matching local key in each service process. Processes running on the host use `localhost`, not `host.docker.internal`. Do not copy real credentials into committed files or command examples. Ensure old `MESSAGING_CONNECTION_STRING` / `AGENT_MEMORY_CONNECTION_STRING` overrides do not point elsewhere.

5. Trust the development certificate and run each service in its own terminal with those bootstrap variables available:

   ```powershell
   dotnet dev-certs https --trust
   dotnet run --project PersonalAgent --launch-profile http
   dotnet run --project PersonalAgent.Web --launch-profile https
   dotnet run --project PersonalAgent.Worker
   ```

   API listens at `http://localhost:5100`; Web HTTPS is `https://localhost:5011`. Register the exact local OAuth callbacks (`/signin-github`, `/signin-google`) in the development provider clients. Run API first to initialize application schema; then Web and Worker.

6. Sign in as an allowed owner, create a chat session, and configure coach assignments at `/admin/integrations` if needed. Verify a coach can see only assigned data. Remove the populated seed-values and generated SQL files after retaining the local key securely.

## Legacy configuration and Compose

If both bootstrap variables are absent, apps retain appsettings/environment bindings and development user secrets. Startup still requires the internal and actor-signing keys, Web OAuth settings/allowlists, and API transcription settings. An OpenAI key and database string alone are not a complete setup after RBAC.

`docker-compose.yml` and `.env.compose.example` describe the manual integration path. They include actor signing and API AssemblyAI settings, make Firebase credentials optional, and wait for API readiness before starting dependents. Real OAuth/provider credentials are still required for that path; it is separate from the verified synthetic stack in `compose.synthetic.yml`.

## Infrastructure helpers

- `start-postgres.ps1` / `stop-postgres.ps1`: local database lifecycle.
- `seed-configuration.ps1`: encrypted application configuration seed.
- `setup-railway.ps1`, `deploy-railway.ps1`, `check-railway-env.ps1`: existing hosted operations; audit against current bootstrap configuration before use.
- Root `Dockerfile.personalagent-api`, `Dockerfile.personalagent-web`, `Dockerfile.personalagent-worker`: canonical service builds.

A full database snapshot is not a public demo fixture. The synthetic stack seeds owner/coach assignments, sessions, vector memory, and speaker review to let contributors reproduce the product without private data.

For existing installations, follow the [transcription configuration rollout](transcription-gateway.md) before starting the refactored API.
