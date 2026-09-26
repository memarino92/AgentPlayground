# C# automation programs

The owner-only, default-disabled `csharp` recipe action compiles saved source against net11.0. Read stdin and write stdout for later steps. Source, package lock and tool declarations are literal and versioned; only `input` expands templates. Internet is enabled on Railway. Programs get no database, Railway, Jev or provider credentials. They can send their supplied input to Internet services; approvals do not inspect arbitrary traffic.

```json
{"steps":[
  {"id":"total","action":"csharp","arguments":{
    "source":"Console.Write(Console.In.ReadToEnd().Split(',').Select(decimal.Parse).Sum());",
    "input":"10,20,30"}},
  {"id":"report","action":"save_report","arguments":{"title":"Total","content":"{{steps.total}}"}}
]}
```

## Railway setup

1. Deploy API and Web from the same revision. Create `personalagent-automation-runner` as a normal Railway service with repository-root build context and `Dockerfile.automation-runner`. No privileged container, Docker socket or public domain is needed. Supply existing `DATABASE_URL` and `CONFIG_ENCRYPTION_KEY` bootstrap; messaging/telemetry use encrypted Shared/AutomationRunner database configuration. Start with one controller (two concurrent messages); replicas multiply concurrency.
2. Deployment watch paths must cover the owning service, `PersonalAgent.Contracts/**`, `PersonalAgent.Integrations/**`, `Directory.*.props`, `global.json` and its Dockerfile; include `PersonalAgent.AutomationRunner/Railway/**` for the controller. Deploy API/Web when contracts/settings UI change. `scripts/deploy-railway.ps1 -IncludeAutomationRunner` includes the already-created service.
3. Prepare a trusted checkpoint. On an operator workstation with Node 22+ and this checkout, run `npm ci --ignore-scripts --no-audit --no-fund` in `PersonalAgent.AutomationRunner/Railway`. Run `node prepare.mjs`, supplying JSON on stdin with `token` (Railway account/workspace token), `environmentId` (UUID) and a unique versioned `checkpoint` name. Construct stdin using a secret prompt/manager; never put credentials in arguments, terminal output or committed files. This command creates a VM, builds the fixed repository Dockerfile, checkpoints it, prints only checkpoint/environment/image identities, and destroys the preparation VM. It incurs Railway usage. Failure reports any known VM needing cleanup. Never reuse a name for different content.
4. As a deployment administrator, open **Settings → Automation runner**. Enter environment UUID, checkpoint name, returned `sha256:…` image ID and the **public HTTPS API origin**, not the Web origin. Replace the token, enable and save. Fresh setup needs no manual database inserts. Reads are masked; stale edits require reload. Settings apply to new runs/gateway requests without restart. Saving validates syntax; verify reachability with the acceptance run below.
5. Under **Integrations and coach access**, enable **C# automation programs** for Owner. Coach is always rejected. Ask chat to create the automation and inspect its completed run before calling it successful.

Railway ISOLATED VMs allow Internet without environment-private networking. The controller uses official `railway` SDK 3.11.0 through a trusted Node adapter with explicit token/environment on create/connect/destroy. The immutable image must already exist in the checkpoint; runtime never pulls an agent-selected image. Missing cgroup/seccomp resource support fails execution closed. Source limit is 24,000 characters within the recipe's 32,768-character total; input is bounded to 65,536 characters. Fixed project compilation does not process file-based app `#:` directives.

## Package and tool approval

Optional `packages` is an array such as `["Newtonsoft.Json@13.0.3"]`; `packageLock` is the literal NuGet `packages.lock.json` contents targeting only net11.0. Generate the lock with a trusted development restore using the same exact dependencies/SDK, then include it in the recipe revision. Automatic lock generation is not implemented. Managed restore uses locked mode and NuGet content hashes. Add **every resolved dependency**, including transitives, to the administrator's exact `ID@version` list. Floating versions/custom feeds are unsupported. Recipe arguments cannot inject project properties or imports.

Optional `tools` declares registered read keys from `automation_catalog`. POST `{ "operationId": "clock-1", "tool": "<catalog key>", "inputs": {} }` to `AUTOMATION_GATEWAY_URL`, using `Authorization: Bearer <AUTOMATION_GATEWAY_TOKEN>`. The response is `{ "output": "…" }`. This per-run/step capability is stored as a hash and expires with the lease. It grants no authority beyond current actor/tool permissions. Reusing an operation ID with changed arguments/review configuration is rejected. At most 20 mediated operations are stored per step. Use subsequent `save_report`/`notify` steps for transactional domain writes.

Jev review has an independent mode:

- **Off:** deterministic permissions/package list apply.
- **Shadow:** records review without blocking.
- **Enforce:** requires confident approval; missing key/content-sharing consent, timeout, malformed response or uncertainty denies the operation.

Configure the existing Jev key, pinned model, timeout, thresholds and `AllowUserContent` consent through its existing settings workflow. Routing can remain Off. Package review sends hashes/dependency metadata, not source bodies; tool arguments are user content. Application permissions apply before model review. Decisions are per run/operation, stored with policy/settings revision and displayed in run details. They do not authorize arbitrary downloads/network calls. Internet-enabled source can download and execute other content inside its sandbox.

## Limits, recovery and visibility

Railway programs use 1 CPU, 512 MiB memory/swap limit, 128 PIDs, 4,096 open file descriptors per process (soft and hard), 256 MiB work tmpfs, 16 MiB temp tmpfs, read-only root, nonroot UID, dropped capabilities and no-new-privileges. Streamed output is capped at 32 KiB. Build/run has a 95-second independent watchdog; SDK exec allows 100 seconds. Provisioning/execution has a three-minute controller deadline. A durable four-minute lease triggers cleanup every 15 seconds; Railway receives a five-minute idle timeout. Two programs execute concurrently per controller.

If compilation reports `MSB3491` / `Too many open files`, check the controller release: the original Docker invocation capped descriptors at 256, which exhausted MSBuild on Railway even after a successful restore. The 4,096 limit is supplied by the controller for each new container, including the local Docker backend. Deploy the updated AutomationRunner service; no checkpoint rebuild or database setting change is required for this limit change. Retry the paused automation and verify compilation, subsequent report steps and VM cleanup before declaring recovery.

The VM ID is persisted before source executes. A lost create response cannot have started source; idle teardown handles that unknown VM. Known VMs are destroyed after completion/failure and reconciled after restart. Failed cleanup remains `CleanupPending` in the dashboard and emits event 4306. Encrypted credential/environment snapshots survive rotation: keep old Railway tokens valid until their leases are destroyed. Destruction clears retained credentials and capability hashes. Provider/controller outages can delay cleanup; inspect Railway Sandboxes and stop affected VMs if recovery fails.

Run/step leases are unique. Stored results survive redelivery; an uncertain claimed execution is never automatically repeated. The five-minute saga watchdog pauses stalled automations. Arbitrary external HTTP effects still need idempotency keys. Pause stops future occurrences; disabling runtime blocks new executions/gateway calls but does not synchronously kill already running code. Current permission is rechecked before successful saga output/domain progression.

The dashboard exposes versions/source, scheduled/past runs, source/image hashes, sandbox ID/state/deadline, bounded diagnostics and operation decisions. OTEL spans include `automation.csharp`, `automation.sandbox.create`, `automation.sandbox.cleanup` and `automation.operation`; existing step metrics report outcome/duration. Sentry/log events 4304–4306 cover program/infrastructure/cleanup failures. Telemetry excludes source, input/output, tool payloads and credentials; private diagnostics remain in the scoped dashboard. Revise source and run/resume to retry; old revisions remain inspectable.

## Verification and rollback

```powershell
docker build -f Dockerfile.automation-sandbox -t agentplayground-csharp-sandbox:1 .
docker compose -f compose.synthetic.yml -f compose.automation-programs.yml up --build --detach --wait
dotnet test PersonalAgent.AutomationRunner.Tests/PersonalAgent.AutomationRunner.Tests.csproj
node --test PersonalAgent.AutomationRunner/Railway/bridge.test.mjs
pwsh -NoProfile -File scripts/tests/Test-AutomationPrograms.ps1
```

Synthetic local execution uses the offline Docker backend only when `SyntheticDemo:Enabled` passes existing Development/local-database guards. This is an existing synthetic bootstrap option, not production configuration. The smoke test verifies SQL transport, compilation, saga/outbox and dashboard flow, temporarily enables/restores the owner's permission, and leaves paused examples. Stop the optional controller afterward. Separate fake-SDK/PostgreSQL tests cover Railway packages, gateway and lifecycle; these do not certify a live account or live Jev quality.

Before production enablement, run a temporary automation fetching a harmless public URL and invoking the declared clock tool. Confirm Internet/gateway success, source/image/sandbox/decision evidence, and VM destruction. Try an unapproved package and Enforce with unavailable Jev; verify no managed install/tool invocation. Interrupt/restart the controller during a long program and verify overdue cleanup. Use disposable inputs and keep redacted operational evidence outside Git. Roll back by disabling runner execution and the Owner C# permission; recipe-only automations continue.
