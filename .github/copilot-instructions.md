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
