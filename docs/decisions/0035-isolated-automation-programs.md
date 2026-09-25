# 0035: Isolated C# computation in agent automations

- Status: Deployment/network policy superseded by [0036](0036-railway-automation-sandboxes.md); retained as the initial Docker implementation record
- Recorded: 2026-09-25
- Decision date: 2026-09-25
- Evidence: User requested generated C# after the registered-recipe PR; `PersonalAgent.AutomationRunner`, `Dockerfile.automation-sandbox`, and automation saga integration.

## Context

Registered recipes provide durable scheduling, authority checks, transactional domain updates and visibility. Generated programs add transformations that cannot be expressed using the small action catalog. Executing arbitrary source inside the API would expose application credentials, filesystem and network authority. Source validation alone cannot contain arbitrary .NET code.

## Decision

Add an owner-only, default-disabled `csharp` recipe action gated by database-backed tool permissions. Persist source inside immutable recipe revisions; templates expand only the explicit stdin input, never source. The API sends bounded program work to a dedicated MassTransit 8.5.8 runner queue through its existing EF outbox. Results rejoin the saga, which rechecks current authority before committing output and continuing domain actions.

The trusted runner controls Docker; application API/Web/Worker and generated code never receive its socket. Each invocation builds and runs inside a fresh nonroot, read-only container with no network, no application secrets, no host mounts, no capabilities, no new privileges, bounded temporary storage, CPU, RAM, processes, output and wall time. A fixed project and offline NuGet configuration compile the single source file against the image's BCL. Source cannot select an SDK, image, package, project or build property. The SDK base digest is pinned; the runner resolves its packaged sandbox image to an immutable image ID at startup and records it with the source hash and diagnostics for each run.

The runner rebuilds from saved source on each occurrence. No compiled binary cache is persisted in this slice. Execution can repeat after uncertain transport acknowledgment; generated computation has no application-side effects. Only later registered steps commit reports/notifications. A result is not a guarantee that arbitrary user code is mathematically deterministic: code can observe clocks or use randomness inside its sandbox.

## Alternatives

- In-process Roslyn or host `dotnet run`: rejected because arbitrary code would inherit application authority.
- Unrestricted file-based SDK directives: rejected because package/project/property directives alter the build graph. A trusted fixed project offers the requested single-source authoring experience without accepting build configuration from the agent. [Microsoft file-based apps reference](https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps).
- MicroVMs or a dedicated remote sandbox provider: stronger separation for hostile multitenant execution; deferred until such deployment is required. Docker shares a kernel and is not an absolute security boundary.
- Persisted compiled artifacts: deferred; evidence identifies compiler image and source today, while future artifact caching requires retention and compiler-upgrade policy.

## Consequences

Operators must deploy a separate runner and explicitly enable the capability. The Docker controller is trusted infrastructure with daemon authority; run it on a dedicated execution host/daemon for hosted use, not a daemon sharing production application workloads. The opt-in Compose override is for disposable local/CI environments. A normal container escape remains a material platform risk, requiring supported/patched host and image releases. Resource constraints must be available on the execution host. [Docker run controls](https://docs.docker.com/engine/containers/run/), [resource limits](https://docs.docker.com/engine/containers/resource_constraints), [tmpfs limits](https://docs.docker.com/engine/storage/tmpfs/).

Credentials remain in the trusted runner's existing encrypted database configuration, scoped to `AutomationRunner` and `Shared`. No new environment settings are introduced. Program authorization reloads through existing live tool-permission reads; sandbox-image policy is part of the runner release and requires rebuilding/restarting the runner. No existing services move to sagas.

## Delivery and verification

Source/revisions, output, compiler image identity, exit code, duration and build/runtime errors appear in scoped automation dashboard data. OTEL and metadata-only Sentry remain integrated. Docker integration tests cover compilation, diagnostics, isolation, resource settings, output limits, cancellation and timeouts. PostgreSQL tests cover capability revocation before commit. The synthetic chat smoke test covers the real SQL transport, isolated runner and transactional report. See [program operations](../runbooks/automation-programs.md) and [roadmap](../plans/roadmap.md).
