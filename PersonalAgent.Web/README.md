# PersonalAgent Web

`PersonalAgent.Web` is the authenticated Blazor Server frontend for the PersonalAgent API.

## What This Service Owns

- GitHub-authenticated chat UI.
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

```text
PERSONAL_AGENT_API_BASE_URL  -> PersonalAgentApi:BaseUrl
INTERNAL_API_KEY             -> PersonalAgentApi:InternalApiKey
GITHUB_CLIENT_ID             -> Authentication:Schemes:GitHub:ClientId
GITHUB_CLIENT_SECRET         -> Authentication:Schemes:GitHub:ClientSecret
```

Optional:

```text
GITHUB_ALLOWED_USERS         -> Authentication:Schemes:GitHub:AllowedUsers
GITHUB_CALLBACK_PATH         -> Authentication:Schemes:GitHub:CallbackPath
```

`PersonalAgentApi:InternalApiKey` is validated on startup in this project, so set it to the same value as API `INTERNAL_API_KEY` when that API protection is enabled.

## Local Run

```powershell
dotnet user-secrets set "PersonalAgentApi:BaseUrl" "http://localhost:5100" --project .\PersonalAgent.Web
dotnet user-secrets set "PersonalAgentApi:InternalApiKey" "your-internal-api-key" --project .\PersonalAgent.Web
dotnet user-secrets set "Authentication:Schemes:GitHub:ClientId" "your-github-client-id" --project .\PersonalAgent.Web
dotnet user-secrets set "Authentication:Schemes:GitHub:ClientSecret" "your-github-client-secret" --project .\PersonalAgent.Web
dotnet run --project .\PersonalAgent.Web
```

## Deployment Notes

- In Railway, prefer private networking from Web to API using `PERSONAL_AGENT_API_BASE_URL`.
- Keep `INTERNAL_API_KEY` synchronized between Web and API.
- The app trusts forwarded host/protocol headers via pipeline configuration for correct OAuth callback handling behind proxy.
