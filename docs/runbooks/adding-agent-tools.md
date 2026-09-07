# Adding agent tools

The API has one executable tool registry in `PersonalAgent/Services/AgentToolRegistry.cs`. `ToolAccessService` derives the admin catalog from it; `AgentToolBinder` derives the agent's functions. A tool's stable key, name, description, role defaults, availability, side-effect metadata, and handler belong to the same registration.

## Add a local capability

1. Implement the domain operation in a service. Keep provider-specific calls inside API adapters. Pass cancellation through async work and use singleton-safe `IBus` for event publication.
2. Add a stable `Local:...` key to `AgentToolKeys`. Existing keys are persisted in permission rows, so renaming one requires a migration decision.
3. Add one `Local(...)` registration with its display label, integration, description, explicit Owner/Coach defaults, `HasSideEffects`, and handler factory. The function name is derived from the key and its argument schema from the delegate. The same description appears in the admin catalog and model tool definition.
4. Bind actor/subject from `AgentAccessContext` inside the factory. Expose only operation inputs to the model. Never accept a model-provided role, actor, or profile as authority. For example, the notification handler accepts title/body while its profile comes from `Access.SubjectProfileId`.
5. Add a meaningful test for the operation and its authorization/subject behavior. The shared registry tests check catalog/function parity and restricted Coach defaults. Only add prompt guidance in chat orchestration when the capability needs an interaction rule beyond its tool description.

Factories form the API composition boundary: their `IServiceProvider` resolves implementation services. Keep business logic in those services. Current tool dependencies are singletons; do not capture a scoped dependency from the root provider. A future scoped operation needs an explicit per-invocation scope/lifetime design.

## Binding and execution

`AgentToolBinder` accepts a server-resolved access context, filters registrations by current permission/availability, and wraps functions with `LoggingAIFunction`. That wrapper checks authorization and availability again immediately before execution, so revoking a permission after binding still prevents the operation. It records the actor, subject, role, tool, duration, and outcome through the existing logging path.

Metadata is descriptive: `HasSideEffects` marks writes, scheduling, sync, notifications, and conservatively all external MCP tools. It does not automatically introduce an approval workflow. Existing permission behavior remains the enforcement mechanism; durable human approval policies are a separate design task.

Tavily functions enter through the same registry and binder. Newly discovered functions default to disabled for both roles. Keys and model-visible function names must be unique across local and external registrations; collisions fail closed rather than ambiguously selecting a handler. An unavailable integration stays unusable even if a permission override is enabled.

The registry does not grant broader subject access or repair the remaining scheduled-task actor propagation issue. Those boundaries remain in the roadmap.

## Verification

```powershell
dotnet test PersonalAgent.Tests/PersonalAgent.Tests.csproj
```

Tests bind every local tool through the production registry, compare catalog/function identity and descriptions, ensure identity fields are absent from argument schemas, verify separate server-bound subjects despite forged input, and prove revoked permissions/unavailable integrations prevent invocation. All side effects in these tests use fakes.
