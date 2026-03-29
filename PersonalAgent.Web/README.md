# PersonalAgent Web

`PersonalAgent.Web` is a modern Razor Components web application that provides an interactive chat interface for the PersonalAgent API. It uses ASP.NET Core with interactive server-side rendering to deliver a responsive, real-time conversational experience.

## Features

- **Interactive Chat Interface**: Real-time message exchange with the AI agent
- **Session Management**: Maintains separate conversation threads with unique session IDs
- **Service Discovery**: Automatically discovers the PersonalAgent API via service configuration
- **Responsive Design**: Works seamlessly across desktop and mobile devices
- **Interactive Server Rendering**: Dynamic updates without full page reloads

## Technology Stack

- **.NET 10.0** with ASP.NET Core
- **Razor Components** with Interactive Server Rendering Mode
- **HttpClient** with dependency injection for API communication
- **QuickGrid** for potential data display components

## Architecture

### Component Structure

- **Chat.razor** - Main interactive chat component with session lifecycle and message handling
- **App.razor** - Root component and shell layout
- **Layout.razor** - Main layout wrapper for all pages
- **Routes.razor** - Route definitions

### Services

- **PersonalAgentClient** - HTTP client wrapper for PersonalAgent API communication
  - `CreateSessionAsync()` - Creates a new conversation session
  - `SendMessageAsync()` - Sends a user message and retrieves agent response

## Configuration

### Environment Variables

#### Required for Deployment

- **`GITHUB_CLIENT_ID`** - GitHub OAuth Client ID (required)
  - Create at: https://github.com/settings/developers
  - See [GitHub Auth Setup](./GITHUB_AUTH_SETUP.md) for detailed instructions

- **`GITHUB_CLIENT_SECRET`** - GitHub OAuth Client Secret (required)
  - Generate in GitHub OAuth app settings
  - Never commit this to version control

- **`PERSONAL_AGENT_API_BASE_URL`** - URL to the PersonalAgent API
  - Defaults to `http://localhost:5100`
  - Use the public Railway URL for the API service

#### Optional Configuration

- **`GITHUB_ALLOWED_USERS`** - Restrict access to specific GitHub usernames (comma-separated)
  - Example: `michael,alice,bob`
  - If not set or empty, all GitHub users can log in
  - Case-insensitive

- **`PERSONAL_AGENT_INTERNAL_API_KEY`** - Internal API key forwarded to the PersonalAgent API
  - Required only if the API sets `INTERNAL_API_KEY`

- **`ASPNETCORE_Environment`** - Runtime environment (Development/Production)
  - Defaults to `Production` in deployed containers
  - Set to `Development` for local development with verbose logging

- **`ASPNETCORE_URLS`** - Server listening address
  - Defaults to `http://+:5000`

### API Endpoint Resolution

The application resolves the PersonalAgent API in this order:

```csharp
Environment.GetEnvironmentVariable("PERSONAL_AGENT_API_BASE_URL")
    ?? builder.Configuration["services:personalagent-api:http:0"]
    ?? "http://localhost:5100";
```

This keeps local Aspire-style discovery working while making Railway deployment explicit.

## Building and Running

### Local Development

```bash
# Development mode with hot reload
dotnet run --project PersonalAgent.Web

# Or with HTTPS
dotnet run --project PersonalAgent.Web --launch-profile https
```

The application will open at `http://localhost:5000` (or `https://localhost:5001` for HTTPS).

### Production Build

```bash
# Build release configuration
dotnet build PersonalAgent.Web -c Release

# Publish for deployment
dotnet publish PersonalAgent.Web -c Release
```

### Docker Deployment

```bash
# Build Docker image
docker build -f PersonalAgent.Web/Dockerfile -t personalagent-web .

# Run container with required environment variables
docker run -d \
  -p 5000:5000 \
  -e GITHUB_CLIENT_ID="your-github-client-id" \
  -e GITHUB_CLIENT_SECRET="your-github-client-secret" \
  -e GITHUB_ALLOWED_USERS="your-github-username" \
  -e PERSONAL_AGENT_API_BASE_URL=http://personalagent-api:5100 \
  personalagent-web
```

**Required variables:**

- `GITHUB_CLIENT_ID`
- `GITHUB_CLIENT_SECRET`

**Optional variables:**

- `GITHUB_ALLOWED_USERS` - Restrict access to specific users
- `PERSONAL_AGENT_API_BASE_URL` - API endpoint (defaults to localhost:5100)
- `PERSONAL_AGENT_INTERNAL_API_KEY` - API key to forward to the backend API

### Railway Deployment

The included `Dockerfile` is optimized for Railway deployment:

1. Multi-stage build reduces final image size
2. Exposes port 5000 for Railway's port binding
3. Sets `ASPNETCORE_ENVIRONMENT=Production` automatically
4. Configurable via environment variables

**Deployment steps:**

1. Connect your GitHub repository to Railway
2. Create a new service and select this repository
3. Set **Project Variables** in Railway dashboard:
    - `GITHUB_CLIENT_ID` = your GitHub OAuth Client ID
    - `GITHUB_CLIENT_SECRET` = your GitHub OAuth Client Secret
    - `GITHUB_ALLOWED_USERS` = your GitHub username (optional, restrict access)
    - `PERSONAL_AGENT_API_BASE_URL` = PersonalAgent API URL on Railway
    - `PERSONAL_AGENT_INTERNAL_API_KEY` = same value as the API's `INTERNAL_API_KEY` when enabled
4. Deploy

See [GitHub Auth Setup](./GITHUB_AUTH_SETUP.md) for detailed GitHub OAuth app creation instructions.

## User Interface

### Chat Page (`/`)

The main page provides:

- **Session Display** - Shows current session ID
- **Message History** - Displays all user and assistant messages in conversation order
- **Message Input** - Text input for sending new messages
- **Control Buttons**:
  - _Start New Chat_ - Creates a new session
  - _Send_ - Submits the current message (disabled if input is empty)
  - _New Chat_ - Resets to create a fresh session

### User Experience Flow

1. User lands on the chat page
2. Click "Start New Chat" to create a new session on the PersonalAgent API
3. Type a message and press Enter or click "Send"
4. Message is sent to the API, response is displayed
5. Conversation history is maintained for the current session
6. Click "New Chat" to start a fresh conversation

## Request Lifecycle

```
User Input
    ↓
Chat.razor component captures message
    ↓
PersonalAgentClient.SendMessageAsync() called
    ↓
HTTP POST to PersonalAgent API (/api/sessions/{sessionId}/messages)
    ↓
API processes through agent, returns response
    ↓
Response deserialized and added to local message history
    ↓
UI re-renders with assistant message
```

## Development Notes

### Adding New Components

When adding new Razor components:

1. Create component files in `Components/` or `Components/Pages/`
2. Use `@page` directive for routable pages
3. Inject `PersonalAgentClient` service as needed
4. Use `@rendermode InteractiveServer` for interactivity

### API Failure Handling

The PersonalAgentClient returns `null` on HTTP failures. Chat component should validate responses:

```csharp
var response = await ApiClient.SendMessageAsync(sessionId, currentMessage);
if (response == null)
{
    // Handle error (display message, retry, etc.)
}
```

### Styling

- CSS files in `wwwroot/css/` are available globally
- Bootstrap or other CSS frameworks can be added via NuGet or CDN
- Component-scoped stylesheets can be created with `.razor.css` files

## Troubleshooting

### "Session not found" errors

- Ensure PersonalAgent API is running and accessible
- Verify `PERSONAL_AGENT_API_BASE_URL` points to the correct API endpoint
- Check API logs for errors processing requests

### Connection timeouts

- PersonalAgentClient has a 30-second timeout per request
- Check network connectivity to PersonalAgent API
- Verify firewall rules allow communication between services

### Components not re-rendering

- Ensure `@rendermode InteractiveServer` is set on interactive components
- Verify `AddInteractiveServerComponents()` is called in Program.cs
- Check browser console for JavaScript errors

## Dependencies

- `Microsoft.AspNetCore.Components.QuickGrid` - Data grid component library (available for future use)

## Additional Resources

- [ASP.NET Core Razor Components Documentation](https://learn.microsoft.com/en-us/aspnet/core/blazor/)
- [Interactive Server Rendering Mode](https://learn.microsoft.com/en-us/aspnet/core/blazor/render-modes)
- [Railway Documentation](https://docs.railway.app/)
- See [PersonalAgent API README](../PersonalAgent/README.md) for backend service details
