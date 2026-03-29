# AGENTS.md - AgentPlayground

## Build Commands

```bash
dotnet build                    # Build entire solution
dotnet build --project <Path>   # Build specific project
dotnet run --project <Path>     # Run specific project
dotnet clean                    # Clean build artifacts
dotnet restore                  # Restore packages
```

## Test Commands

```bash
dotnet test                              # Run all tests
dotnet test --project <Path>             # Run specific test project
dotnet test --filter "FullyQualifiedName~TestMethodName"  # Run single test
```

## Project Management

```bash
dotnet add <Path> package <PackageName>    # Add NuGet package
dotnet add <Path> reference <RefPath>      # Add project reference
dotnet remove <Path> package <PackageName> # Remove package
dotnet sln add <Path>                      # Add to solution
```

## Code Style Guidelines

### General Principles

- Prefer **expression-bodied members** for concise logic
- Use **one-line if statements without braces** for simple conditions
- Favor **early returns** to avoid nesting
- Leverage **switch expressions** over traditional switch statements
- Use **collection expressions** (`[item1, item2]`, `[..existing, ..new]`)
- Adopt **immutable data structures** (`record`, `record struct`)
- Use **`with` expression** for modified copies

### Naming Conventions

- **Types/Methods/Fields**: PascalCase (`AgentService`, `CreateSessionAsync`)
- **Parameters**: PascalCase
- **Files**: Match the type name (`AgentService.cs` contains `AgentService`)
- **Constants**: PascalCase (`MaxMessageLength`)

### Types and Properties

```csharp
// Immutable data contract
internal record ConversationMessage(string Role, string Content);

// DI binding requires {get;set;}
internal record ApiKeyOptions
{
    public string OpenAiKey { get; set; } = string.Empty;
}

// Use {get;init;} for immutable DTOs
public record ResponseDto(string Name) { public DateTime Created { get; init; } }
```

### Pattern Examples

```csharp
// Expression-bodied members
public bool IsValid => !string.IsNullOrEmpty(Name);
public string GetStatus() => status switch
{
    StatusEnum.Active => "Running",
    StatusEnum.Inactive => "Stopped",
    _ => "Unknown"
};

// One-line if statements
if (condition) DoSomething();

// Early returns
public void Process()
{
    if (!IsValid) return;
    if (IsCancelled) return;
    // Main logic
}
```

## Import Organization

1. System namespaces first
2. Third-party (Microsoft, OpenAI, etc.)
3. Project namespaces
4. Blank line between groups

```csharp
using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using PersonalAgent.Configuration;
using PersonalAgent.Models;
```

## Error Handling & Logging

- Inject `ILogger<T>` via constructor
- Use **structured logging** with named parameters
- Log exceptions with context

```csharp
_logger.LogInformation("Processing message for session {SessionId}", sessionId);
_logger.LogError(ex, "Failed to process message for session {SessionId}", sessionId);
```

Log levels: `LogTrace` < `LogDebug` < `LogInformation` < `LogWarning` < `LogError` < `LogCritical`

## Configuration Pattern

Use `IOptions<T>` with environment variable precedence:

```csharp
services.AddOptions<ApiKeyOptions>()
    .Configure(opts =>
    {
        opts.OpenAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? configuration["OpenApiKey"]
            ?? string.Empty;
    });
```

## Project Structure

```
AgentPlayground/
├── AgentPlayground.slnx           # Solution file
├── Directory.Build.props           # TreatWarningsAsErrors=true
├── Directory.Packages.props        # Central package versioning
├── PersonalAgent/                  # Minimal API agent
├── PersonalAgent.Web/              # Blazor Server frontend
└── PersonalAgent.AppHost/          # Aspire orchestration
```

## Important Settings

- `TreatWarningsAsErrors` is **enabled** - never add `<NoWarn>` without approval
- Package versions are **centrally managed** in `Directory.Packages.props`
- **.NET**: 10.0
- **Framework**: Microsoft Agent Framework (RC 1.0)
