# PersonalAgent Web

`PersonalAgent.Web` is the authenticated Blazor Server frontend for the PersonalAgent API.

## What This Service Owns

- GitHub-authenticated owner UI and Google-authenticated coach transcript access.
- Session list, session restore, and transcript browsing.
- Model selection when creating a chat session.
- Test-event publish action wired to the shared bus.
- API access via `PersonalAgentClient` with internal API key header forwarding.

## Runtime Model

- ASP.NET Core Razor Components with `InteractiveServer` rendering.
- Main page: `PersonalAgent.Web/Components/Pages/Chat.razor`.
- API access wrapper: `PersonalAgent.Web/Services/PersonalAgentClient.cs`.

The web app talks only to the API; it does not call model providers or worker services directly.

## Required Configuration

Production requires only the following bootstrap variables after running
`scripts/seed-configuration.ps1`:

```text
DATABASE_URL
CONFIG_ENCRYPTION_KEY
```

The API URL, OAuth settings, allowlists, and shared signing credentials are loaded from
the encrypted PostgreSQL configuration table. Existing direct environment bindings below
remain available when database-backed configuration is disabled locally.

```text
PERSONAL_AGENT_API_BASE_URL  -> PersonalAgentApi:BaseUrl
INTERNAL_API_KEY             -> PersonalAgentApi:InternalApiKey
WEB_ACTOR_SIGNING_KEY        -> PersonalAgentApi:ActorSigningKey (Web) and Security:ActorSigningKey (API)
GITHUB_CLIENT_ID             -> Authentication:Schemes:GitHub:ClientId
GITHUB_CLIENT_SECRET         -> Authentication:Schemes:GitHub:ClientSecret
GOOGLE_CLIENT_ID             -> Authentication:Schemes:Google:ClientId
GOOGLE_CLIENT_SECRET         -> Authentication:Schemes:Google:ClientSecret
```

Access allowlists are required and fail closed:

```text
GITHUB_ALLOWED_USERS         -> Authentication:Schemes:GitHub:AllowedUsers
GITHUB_CALLBACK_PATH         -> Authentication:Schemes:GitHub:CallbackPath
GOOGLE_ALLOWED_EMAILS        -> Authentication:Schemes:Google:AllowedEmails
GOOGLE_CALLBACK_PATH         -> Authentication:Schemes:Google:CallbackPath
```

`PersonalAgentApi:InternalApiKey` and `PersonalAgentApi:ActorSigningKey` are validated on startup. Set `WEB_ACTOR_SIGNING_KEY` to the same long random value in Web and API; never expose it to browser or mobile clients.

## Production Seed

```powershell
Copy-Item .\scripts\seed-configuration.values.ps1.example .\scripts\seed-configuration.values.ps1
# Edit the ignored values file, then generate SQL:
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\seed-configuration.ps1
```

Paste `scripts/seed-configuration.generated.sql` into the PostgreSQL console. After
seeding, delete the populated values and generated SQL files. Keep `DATABASE_URL` and
`CONFIG_ENCRYPTION_KEY` on API, Web, and Worker.

## Local Run

```powershell
dotnet user-secrets set "PersonalAgentApi:BaseUrl" "http://localhost:5100" --project .\PersonalAgent.Web
dotnet user-secrets set "PersonalAgentApi:InternalApiKey" "your-internal-api-key" --project .\PersonalAgent.Web
dotnet user-secrets set "PersonalAgentApi:ActorSigningKey" "a-long-random-signing-key" --project .\PersonalAgent.Web
dotnet user-secrets set "Authentication:Schemes:GitHub:ClientId" "your-github-client-id" --project .\PersonalAgent.Web
dotnet user-secrets set "Authentication:Schemes:GitHub:ClientSecret" "your-github-client-secret" --project .\PersonalAgent.Web
dotnet user-secrets set "Authentication:Schemes:Google:ClientId" "your-google-client-id" --project .\PersonalAgent.Web
dotnet user-secrets set "Authentication:Schemes:Google:ClientSecret" "your-google-client-secret" --project .\PersonalAgent.Web
dotnet user-secrets set "Authentication:Schemes:Google:AllowedEmails" "coach@example.com" --project .\PersonalAgent.Web
dotnet run --project .\PersonalAgent.Web
```

## Coach Access

Create a Google OAuth 2.0 web client in Google Cloud and register the redirect URI
`https://yourdomain.com/signin-google` (or the local HTTPS URL plus `/signin-google`).

Set `GOOGLE_ALLOWED_EMAILS` to the coach's exact Google account email. Only listed,
verified Google email addresses receive the `Coach` role. An owner must then assign the
coach email to an athlete profile at `/admin/integrations`. Coaches receive private chat
sessions and can query only assigned coach check-ins; tool access is controlled by the
role matrix on that page. GitHub sign-ins retain the `Owner` role.

## Deployment Notes

- In Railway, prefer private networking from Web to API using `PERSONAL_AGENT_API_BASE_URL`.
- Keep `INTERNAL_API_KEY` synchronized between Web and API.
- The app trusts forwarded host/protocol headers via pipeline configuration for correct OAuth callback handling behind proxy.
