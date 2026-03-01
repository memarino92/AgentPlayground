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
├── AgentPlayground.slnx           # Solution file
├── .github/
│   └── copilot-instructions.md    # Copilot guidelines for this workspace
├── README.md                       # This file
├── MyFirstAgent/                  # Simple agent getting started example
│   ├── MyFirstAgent.csproj
│   ├── Program.cs
│   └── ...
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

### 5. Run a Project

```bash
dotnet run --project MyFirstAgent
```

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
