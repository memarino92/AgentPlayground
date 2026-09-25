# 0036: Railway sandboxes and mediated automation operations

- Status: Accepted; implementation in PR #73, live Railway verification pending
- Recorded / decision date: 2026-09-25
- Evidence: user requested Railway create/destroy, Internet access and optional Jev approval; implementation in the automation runner, runtime settings and operation gateway.
- Supersedes: deployment and network policy in [0035](0035-isolated-automation-programs.md). Recipe/saga/outbox design remains unchanged.

## Context

Railway Sandboxes became generally available in September 2026. They provide isolated VMs, Docker inside the VM, prepared checkpoints, file transfer, exec and destruction. ISOLATED networking permits outbound Internet without access to the Railway environment's private network. Sources: [announcement](https://railway.com/changelog/2026-09-18-railway-sandboxes), [reference](https://docs.railway.com/sandboxes), and installed official railway SDK 3.11.0 types/source.

## Decision

Use a trusted Railway service running the .NET controller and a small Node adapter to the pinned official SDK. Create an ISOLATED VM per run from an administrator-prepared checkpoint. Run source inside the pinned Docker image with CPU, memory, PID, filesystem, output and watchdog limits. Internet remains enabled. No Docker socket or privileged container is needed on the controller service.

Persist a unique run/step lease before provisioning and the VM ID before execution. Record results before transport acknowledgment; redelivery reuses stored results and never reclaims an uncertain execution. Reconcile known overdue VMs after restart. Unknown create outcomes cannot have started untrusted code and expire through idle teardown. Retain encrypted cleanup credentials and environment identity across rotation, then clear credentials after cleanup. Domain updates remain in the API's EF Core saga/outbox transaction; remote execution is not part of that database transaction. MassTransit remains 8.5.8.

The sandbox receives source/input and a short-lived run/step capability. The public HTTPS gateway verifies that capability, current run state, owner identity, C# permission, declared read tools and current individual tool permission. Domain writes still use later recipe steps. Operation IDs, request hashes, a 20-operation budget and stored results make duplicate reads stable after commit. A crash before commit can repeat a read; external Internet effects have no exactly-once guarantee.

Managed NuGet restore accepts exact direct versions plus a literal lock file. Every resolved dependency must be approved in administrator settings; restore uses locked mode against the fixed NuGet feed. Package code/build targets execute only inside the container. Jev review is additional: Off uses deterministic checks; Shadow records without blocking; Enforce requires confident approval and fails closed when unavailable. Review uses separate mode selection with the existing key, model, thresholds and explicit content-sharing consent. It cannot expand permissions. Reviews occur per run/operation, not as a lifetime trust grant for a source version.

Configuration is database-first with initial-setup UI, encrypted token, keep/replace/clear semantics and optimistic concurrency. New runs/gateway requests read live settings. Operation evidence and sandbox state/deadlines join source, versions, schedules, diagnostics, OTEL, logs and Sentry in the dashboard/operations workflow.

## Alternatives and consequences

- Ordinary Railway service per program: unnecessary now that native VM lifecycle exists.
- External Docker host: retained only as a guarded local synthetic backend.
- Block all Internet or proxy every request: deferred at user direction. Managed approvals do not intercept arbitrary downloads/HTTP calls made by source. Programs can send their input to Internet services.
- In-process execution: rejected because it inherits application authority.
- Arbitrary application tools: not introduced; gateway starts with cataloged read tools.

Source revision, image ID and lock identify inputs; Internet responses, clocks, randomness and optional Jev decisions are not deterministic. VM/resource support and paid lifecycle behavior require a live Railway acceptance run. Checkpoint preparation is an operator task. Automatic lock generation, human pending-approval queues, binary caching and enforced egress proxy remain future work.

## Delivery and verification

PostgreSQL tests cover encrypted settings, conflicts, revocation, scoped/expired capabilities, denied transitive packages, duplicate operations and cleanup leases. Runner tests use a fake SDK boundary with real PostgreSQL plus real local Docker isolation. Node tests cover SDK calls, network policy, source handling, limits and locked restore. Blazor tests cover initial setup and token removal. These do not claim live Railway or Jev evaluation. See [operations](../runbooks/automation-programs.md).
