# AgentPlayground Copilot Instructions

## Project Overview

This is an exploration workspace for creating agents with the **Microsoft Agent Framework** (currently in RC for v1.0). The solution contains multiple projects, some related and some unrelated, serving as an experimental playground for understanding agent capabilities and patterns.

## Technology Stack

- **Framework**: Microsoft Agent Framework (RC 1.0)
- **.NET Version**: Latest stable and pre-release versions (currently targeting .NET 10.0 and later)
- **Language**: C# with modern language features
- **Input**: User-driven exploration—accept user input on project capabilities and AI agent implementations

## Code Style Guidelines

When writing C# code in this workspace, follow these modern, functional-informed principles:

### General Approach

- Prefer **expression-bodied members** for concise, readable logic
- Use **one-line if statements without braces** where the condition and action are simple
- Favor **early returns** to avoid deeply nested if statements
- Leverage **switch expressions** instead of traditional switch statements
- Utilize **collection expressions** for cleaner list/array initialization
- Favor **immutable data structures**, especially `record` and `record struct` for domain/data contracts
- Prefer creating modified copies with the **`with` expression** instead of mutating existing instances
- Adopt an **expressive, modern style** rather than historical procedural patterns
- Maintain **visual appeal** and readability as a priority

### Examples

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
    // Main logic here
}

// Collection expressions
var items = [item1, item2, item3];
var merged = [..existing, ..new];

// Switch expressions
var result = input switch
{
    null => "Empty",
    "" => "Blank",
    _ => input.Trim()
};
```

## Workspace Structure

- **Solution Root**: `AgentPlayground.slnx`
- **Projects**: Each project resides in its own directory with a `.csproj` file
- **Current Projects**: `MyFirstAgent` (console sample), `PersonalAgent` (minimal API personal assistant)
- **Build Output**: Standard `bin/` and `obj/` directories per project
- **NuGet Dependencies**: Managed via project file references and package files

## Maintenance Guidelines

### Updating This File

Whenever you make **significant changes** to the solution or its projects, update this `copilot-instructions.md` file to reflect:

- New projects added to the solution
- Changes to the .NET version target
- New frameworks or dependencies introduced
- Changes to the build process or configuration
- Updates to coding style preferences or architectural patterns

### Updating the README

Similarly, update the `README.md` (or create one if missing) to document:

- Project structure and organization
- How to build and run the solution
- New projects and their purposes
- Setup instructions for developers
- Links to relevant Microsoft Agent Framework documentation

### When to Update

Update these documentation files when:

1. **Adding a new project** to the solution
2. **Changing the build process** or adding build steps
3. **Updating .NET target framework versions**
4. **Introducing new frameworks or major dependencies**
5. **Significantly shifting architectural patterns** or approach
6. **Changing how the solution is structured or organized**

## Configuration & Dependency Injection Patterns

### IOptions<T> for Configuration

When binding configuration to C# objects, prefer the `IOptions<T>` pattern with explicit property control:

```csharp
// Configuration record with mutable properties (required for DI binding)
internal record ApiKeyOptions
{
    public string OpenAiKey { get; set; } = string.Empty;  // {get;set;} for DI Configure() binding
}

// Service registration
services.AddOptions<ApiKeyOptions>()
    .Configure(opts =>
    {
        opts.OpenAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? configuration["OpenApiKey"]
            ?? string.Empty;
    });

// Injection into classes
public class MyService
{
    public MyService(IOptions<ApiKeyOptions> options)
    {
        var key = options.Value.OpenAiKey;
    }
}
```

**Important Note**: Use `{get;set;}` properties (not `{get;init;}`) on records when they will be bound via DI's `Configure()` method, since that pattern assigns values post-construction. For models used only as immutable data transfer objects, `{get;init;}` is appropriate.

### Environment Variable Resolution Strategy

- **Development**: Use `dotnet user-secrets` for sensitive keys (not persisted in version control)
- **Production**: Set environment variables at container/deployment runtime
- **Resolution order**: Check environment variables first, fall back to configuration files, then defaults

## Logging Guidelines

### Using ILogger<T> Responsibly

Always inject `ILogger<T>` via constructor dependency injection. Use structured logging with named parameters for better observability and querying, even if no specialized logging pipeline is configured.

**Best Practices:**

```csharp
public class AgentService
{
    private readonly ILogger<AgentService> _logger;

    public AgentService(ILogger<AgentService> logger)
    {
        _logger = logger;
    }

    public async Task<string> ProcessMessage(string sessionId, string message)
    {
        // Use structured logging with named parameters
        _logger.LogInformation("Processing message for session {SessionId}", sessionId);

        try
        {
            var result = await CallAgent(message);
            _logger.LogDebug("Agent returned {CharacterCount} characters", result.Length);
            return result;
        }
        catch (Exception ex)
        {
            // Log exceptions with context
            _logger.LogError(ex, "Failed to process message for session {SessionId}", sessionId);
            throw;
        }
    }
}
```

**Log Level Guidelines:**

- **LogTrace**: Internal method flow, low-level debugging (rarely used in production)
- **LogDebug**: Diagnostic information useful during local development
- **LogInformation**: General flow of the application (session created, request started, etc.)
- **LogWarning**: Unexpected but recoverable situations (fallback used, retry attempted)
- **LogError**: Failures that prevent a specific operation from completing
- **LogCritical**: Application-wide failures requiring immediate attention

**Structured Logging Best Practices:**

- ✅ Use named parameters: `_logger.LogInformation("User {UserId} created session {SessionId}", userId, sessionId)`
- ❌ Avoid string interpolation: `_logger.LogInformation($"User {userId} created session {sessionId}")`
- ✅ Log actions and outcomes: `"Processing message"`, `"Message processed successfully"`
- ❌ Avoid logging sensitive data: API keys, passwords, PII without redaction
- ✅ Include correlation IDs or session IDs for tracing across operations
- ✅ Log exceptions with the exception object as first parameter: `_logger.LogError(ex, "Message", params)`

**Performance Considerations:**

Use log level checks for expensive operations:

```csharp
if (_logger.IsEnabled(LogLevel.Debug))
{
    var diagnosticData = ExpensiveSerializationMethod(obj);
    _logger.LogDebug("Diagnostic data: {Data}", diagnosticData);
}
```

## Project Management with dotnet CLI

**Always prefer the `dotnet` CLI for project and solution management** over directly editing `.csproj` and `.slnx` files. This ensures consistency and reduces the risk of file corruption.

### Common Tasks

**Add a NuGet package to a project:**

```bash
dotnet add <ProjectPath> package <PackageName>
```

**Add a project reference:**

```bash
dotnet add <ProjectPath> reference <ReferencePath>
```

**Remove a NuGet package:**

```bash
dotnet remove <ProjectPath> package <PackageName>
```

**Create a new project:**

```bash
dotnet new console -n <ProjectName> -f net10.0
dotnet sln add <ProjectPath>
```

For API-style agents, use:

```bash
dotnet new web -n <ProjectName> -f net10.0
dotnet sln add <ProjectPath>
```

**List all projects in the solution:**

```bash
dotnet sln list
```

### When to Edit Files Directly

Only edit `.csproj` or `.slnx` files directly when:

- The dotnet CLI doesn't provide the needed functionality
- You're making structural changes that require custom MSBuild logic
- You're modifying project metadata or build properties not exposed via CLI

Always ensure changes are validated with `dotnet build` after editing.

## Build Configuration and Warnings

### Treat Warnings as Errors

This workspace uses `TreatWarningsAsErrors` in `Directory.Build.props` to enforce code quality. All compiler and package warnings must be resolved—they cannot be ignored.

### Warning Suppression Policy

**CRITICAL**: Never add warning codes to the `<NoWarn>` list without explicit user approval.

When encountering a warning:

1. **First priority**: Fix the underlying issue (remove unnecessary packages, correct code, etc.)
2. **If the warning cannot be resolved**: Explain the warning to the user and ask whether to:
   - Fix it another way
   - Suppress it with justification
   - Leave it as a build error until proper resolution

**Example dialogue:**

```
"Build warning NU1510: PackageReference 'X' can be removed.
Should I:
1. Remove the package reference (recommended)
2. Add NU1510 to NoWarn list
3. Investigate further"
```

**Never** assume a warning should be suppressed. The user decides the trade-offs.

### Central Package Management

Package versions are managed in `Directory.Packages.props`. Individual projects reference packages without version attributes:

```xml
<!-- Directory.Packages.props -->
<PackageVersion Include="Microsoft.Agents.AI" Version="1.0.0-rc1" />

<!-- Project.csproj -->
<PackageReference Include="Microsoft.Agents.AI" />
```

This ensures consistent versions across all projects and simplifies dependency updates.

## Building and Running

Projects in this solution should build successfully with:

```bash
dotnet build
dotnet run --project <ProjectPath>
```

Specific project build or run instructions should be documented in `README.md`.

## Additional Notes

- This is an **exploration/playground** workspace—experimental code and patterns are expected
- Embrace the latest C# language features for clean, modern code
- Prioritize code clarity and expressiveness in all implementations
- Refer to the current Microsoft Agent Framework documentation for framework-specific patterns
