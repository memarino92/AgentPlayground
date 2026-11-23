# Sandbox automation programs

The owner-only, default-disabled `csharp` recipe action compiles saved source against net11.0. Read stdin and write stdout for later steps. Source, package lock and tool declarations are literal and versioned; only `input` expands templates. Internet is enabled on Railway. Programs get no database, Railway, Jev or provider credentials. They can send their supplied input to Internet services; approvals do not inspect arbitrary traffic.

```json
{"steps":[
  {"id":"total","action":"csharp","arguments":{
    "source":"Console.Write(Console.In.ReadToEnd().Split(',').Select(decimal.Parse).Sum());",
    "input":"10,20,30"}},
  {"id":"report","action":"save_report","arguments":{"title":"Total","content":"{{steps.total}}"}}
]}
```

For an immediate one-time computation, grant Owner **Run Railway sandbox** as well as **Sandbox programs** and **Create or revise automation**. The `run_railway_sandbox` agent tool takes a name, literal source, an optional language (csharp, shell or dockerfile), stdin input and a stable UUID `requestKey`, with optional locked packages and declared read tools. It saves a single-step run with no future schedule, then returns its automation ID, run ID and dashboard link. Repeating the same key and source returns the existing run; changing the source for that key fails. `inspect_automation` shows output, diagnostics and cleanup state. Use **Implement platform change** for repository coding jobs, which have their own checkpoint and publication workflow.

## Research jobs and interactive results

For a one-time analysis, call `run_railway_sandbox` with `language: "dockerfile"` and supply the Dockerfile as `source`. For a recurring run, use `save_automation` with the same source and input in a `dockerfile` recipe action. The Worker builds from public images and package sources inside the disposable Railway VM. The build context contains only the Dockerfile. Embed program files using Dockerfile heredocs. The resulting image runs its own CMD and ENTRYPOINT with the supplied stdin as UID 65532. Use `/tmp` or `/scratch` for temporary files. Use image digests and pinned package versions when repeatability matters.

The build has eight minutes, execution has five minutes and the controller has fifteen minutes including provisioning. The VM bounds the build. Runtime has 512 MiB of RAM, one CPU and 32 KiB combined output. Build errors retain the tail of the build log. Successful runs record the local image ID. Destroying the VM removes the image and build cache. Scheduled runs rebuild from the saved Dockerfile. Custom builds require Railway. For simple jobs use `shell` to run POSIX shell directly in the checkpoint image.

To return an interactive result, write a complete HTML document beginning with `<!doctype html>` to stdout. Embed data, styles and JavaScript. Open the returned dashboard link in the authenticated phone browser and tap **Open interactive result**. The viewer uses an opaque-origin sandbox with inline scripts and styles and data-URL images. Ordinary output remains available as text. Changes to the installed mobile client go through the platform coding PR and release workflow.

### Live Python in chat

Select the prepared checkout's checkpoint name and image ID in Railway sandbox settings. The verified 2026-11-19 checkpoint for the AgentPlayground production environment is `scratchpad-06730b4fb5e84febb17610710f511e5d`, image `sha256:395d59cf1f415b69894aa9370f5ba31218e0eb3e5314570855e7938a9cb9d36e`. For another environment, build a fresh checkpoint there with the preparation script. The image includes Python, NumPy, pandas and Matplotlib. Deploy API, Web and Worker together. Grant Owner **Python scratchpad** for live cells. To promote a calculation, also grant **Schedule scratchpad calculation**, **Create or revise automation** and **Sandbox programs**.

Ask “Create a DataFrame of 1, 3 and 5 and call it df.” Then ask “Use df to plot its cumulative sum.” The second cell uses the live interpreter in the same conversation. Output streams into the current turn, and the run dashboard retains the result and previews the exported plot. The native automation run screen opens charts and files in the authenticated browser. Runtime exceptions retain variables for corrections. Ten minutes of inactivity, a timeout or a runtime configuration change starts a fresh session on the next request. A cell has a 30-second execution deadline. Delayed work has a run link for inspection.

“Run that calculation every day” saves the successful cells through the chosen run in execution order. The schedule records the checkpoint image digest. The fresh interpreter reads those cells and rebuilds their variables. If the selected runtime image changes, the job reports ImageChanged so the owner can validate and promote the calculation again. Code that fetches a dataset fetches it again. Files supplied outside those cells, changing data and manually installed dependencies require an explicit reproducible input or setup step. If the session includes a failed cell, save a consolidated Python recipe. Repeating the same promotion with the same name, timestamp and interval returns the existing schedule.

For a live CLI timing check, run `scripts/test-railway-scratchpad.ps1 -EnvironmentName <environment>` from a linked checkout. It creates a billed isolated VM, builds the runtime, checks DataFrame reuse over 20 calls and validates a PNG. Pass `-PrepareCheckpoint` to retain a new, clean checkpoint before the proof writes sample data, or `-Checkpoint <name>` to test an existing proof checkpoint. It destroys its VM on exit. CLI round-trip timing excludes app dispatch, chat inference and device rendering.

### Export files

### File inputs

In web chat, select **Attach sandbox input** and send the message containing the attachment reference. Both `run_python` and `run_railway_sandbox` accept an optional `inputFiles` array. Saved program recipe steps accept the same argument. For example:

```json
[{ "name": "sales.csv", "reference": "attachment:11111111-1111-1111-1111-111111111111" }]
```

Read the file at `/inputs/sales.csv`. To reuse a previous export, use `artifact:<run UUID>:<step index>:<file index>` with zero-based indices from the run manifest. The worker resolves files against the receiving actor and profile. The directory is read-only inside the container. A cell can add files to its warm session. Promoting a session records the input references for fresh scheduled executions. Use distinct names across cells when promoting multiple files. One execution accepts up to 16 files totaling 16 MiB. Custom images receive inputs when their entrypoint runs.

Place finished files under `/artifacts` in a Railway C#, shell or Dockerfile step. A custom image can also populate this directory at build time, for example with `COPY --from=builder /app/dist/ /artifacts/`. At runtime the job can add results such as a PDF, a Parquet dataset or an entire static website. Each step exports its own directory after a successful exit.

Open **Automations → Inspect run → Railway sandboxes** to download a file or the full ZIP with relative paths. Run inspection returns the manifest with paths and byte sizes. Downloads work after the VM is destroyed. The console output budget is independent of the 64 MiB and 512-file export budget per step. Archive regular files and directories with portable names. Links and special files fail export. Files persist in PostgreSQL alongside the run result, so recurring exports contribute to database and backup size.

## Railway setup

1. Deploy API, Web and Worker from the same revision. The Worker runs the sandbox consumers and cleanup reconcilers. It needs the existing `DATABASE_URL` and `CONFIG_ENCRYPTION_KEY` bootstrap and encrypted Shared/Worker messaging configuration. Copy any service-scoped settings still needed from the old `AutomationRunner` scope to `Worker` before retiring the old service. The Worker needs no privileged container, Docker socket or public domain in production. Stop the old runner before enabling the new Worker deployment so both do not consume the same queues. Confirm pending cleanup leases are handled by the Worker, then retire the old Railway service.
2. Worker deployment watch paths must cover `PersonalAgent.Worker/**`, `PersonalAgent.Contracts/**`, `PersonalAgent.Integrations/**`, `Directory.*.props`, `global.json` and `Dockerfile.personalagent-worker`. Deploy API/Web when contracts, settings UI or agent tools change. `scripts/deploy-railway.ps1` deploys the Worker with the regular services. Each Worker replica can consume two program messages and one coding job; existing database claims prevent duplicate coding execution.
3. Prepare a trusted checkpoint. On an operator workstation with Node 22+ and this checkout, run `npm ci --ignore-scripts --no-audit --no-fund` in `PersonalAgent.Worker/Railway`. Run `node prepare.mjs`, supplying JSON on stdin with `token` (Railway account/workspace token), `environmentId` (UUID) and a unique versioned `checkpoint` name. Construct stdin using a secret prompt/manager; never put credentials in arguments, terminal output or committed files. This command creates a VM, builds the fixed repository Dockerfile, checkpoints it, prints only checkpoint/environment/image identities, and destroys the preparation VM. It incurs Railway usage. Failure reports any known VM needing cleanup. Never reuse a name for different content.
4. As a deployment administrator, open **Settings → Railway sandboxes**. Enter environment UUID, checkpoint name, returned `sha256:…` image ID and the **public HTTPS API origin**, not the Web origin. Replace the token, enable and save. Fresh setup needs no manual database inserts. Reads are masked; stale edits require reload. Settings apply to new runs/gateway requests without restart. Saving validates syntax; verify reachability with the acceptance run below.
5. Under **Integrations and coach access**, enable **Sandbox programs** for Owner. Coach is always rejected. Ask chat to create the automation and inspect its completed run before calling it successful.

Railway ISOLATED VMs allow Internet without environment-private networking. The controller uses official `railway` SDK 3.11.0 through a trusted Node adapter with explicit token/environment on create/connect/destroy. The immutable image must already exist in the checkpoint; the csharp and shell actions use this image. Missing cgroup/seccomp resource support fails execution closed. Source limit is 24,000 characters within the recipe's 32,768-character total; input is bounded to 65,536 characters. Fixed project compilation does not process file-based app `#:` directives.

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

If compilation reports `MSB3491` / `Too many open files`, check the Worker release: the original Docker invocation capped descriptors at 256, which exhausted MSBuild on Railway even after a successful restore. The 4,096 limit is supplied by the Worker for each new container, including the local Docker backend. Deploy the updated Worker; no checkpoint rebuild or database setting change is required for this limit change. Retry the paused automation and verify compilation, subsequent report steps and VM cleanup before declaring recovery.

The VM ID is persisted before source executes. A lost create response cannot have started source; idle teardown handles that unknown VM. Known VMs are destroyed after completion/failure and reconciled after restart. Failed cleanup remains `CleanupPending` in the dashboard and emits event 4306. Encrypted credential/environment snapshots survive rotation: keep old Railway tokens valid until their leases are destroyed. Destruction clears retained credentials and capability hashes. Provider/controller outages can delay cleanup; inspect Railway Sandboxes and stop affected VMs if recovery fails.

Run/step leases are unique. Stored results survive redelivery; an uncertain claimed execution is never automatically repeated. The five-minute saga watchdog pauses stalled automations. Arbitrary external HTTP effects still need idempotency keys. Pause stops future occurrences; disabling runtime blocks new executions/gateway calls but does not synchronously kill already running code. Current permission is rechecked before successful saga output/domain progression.

The dashboard exposes versions/source, scheduled/past runs, source/image hashes, sandbox ID/state/deadline, bounded diagnostics and operation decisions. OTEL spans include `automation.csharp`, `automation.sandbox.create`, `automation.sandbox.cleanup` and `automation.operation`; existing step metrics report outcome/duration. Sentry/log events 4304–4306 cover program/infrastructure/cleanup failures with operation IDs and exception details. Traces exclude source, input/output and tool payloads. Sentry errors can include application details from logged exceptions; credential patterns are masked. Private diagnostics remain in the scoped dashboard. Revise source and run/resume to retry; old revisions remain inspectable.

For a Railway bridge failure, read the Worker runtime log around the run and search for its UUID or `Railway bridge failed`. Event 4307 reports the run ID, step, failed stage, and a fixed reason, with an HTTP status, WebSocket close code, or process exit code when available. An `upload-input: connection-reset` event points to attachment transfer. A `pause-scratchpad: container-not-found` event points to artifact export after execution. Unrecognized failures use `unexpected-error`. Raw SDK messages, response bodies, commands, filenames, and program output are excluded from this event. Program output and errors remain available in the scoped run dashboard.

## Verification and rollback

```powershell
docker build -f Dockerfile.automation-sandbox -t agentplayground-csharp-sandbox:1 .
docker compose -f compose.synthetic.yml -f compose.automation-programs.yml up --build --detach --wait
dotnet test PersonalAgent.Worker.Tests/PersonalAgent.Worker.Tests.csproj --filter "FullyQualifiedName~Sandboxes"
node --test PersonalAgent.Worker/Railway/bridge.test.mjs PersonalAgent.Worker/Railway/coding.test.mjs
pwsh -NoProfile -File scripts/tests/Test-AutomationPrograms.ps1
```

Synthetic local execution uses the offline Docker backend only when `SyntheticDemo:Enabled` and `SyntheticDemo:AutomationProgramsEnabled` pass existing Development/local-database guards. The Compose override grants the Worker a disposable local Docker socket; production needs no socket. The smoke test verifies SQL transport, compilation, saga/outbox and dashboard flow, temporarily enables/restores the owner's permission, and leaves paused examples. Separate fake-SDK/PostgreSQL tests cover Railway packages, gateway and lifecycle; these do not certify a live account or live Jev quality.

Before production enablement, run a temporary automation fetching a harmless public URL and invoking the declared clock tool. Confirm Internet/gateway success, source/image/sandbox/decision evidence, and VM destruction. Try an unapproved package and Enforce with unavailable Jev; verify no managed install/tool invocation. Interrupt/restart the controller during a long program and verify overdue cleanup. Use disposable inputs and keep redacted operational evidence outside Git. Roll back by disabling runner execution and the Owner C# permission; recipe-only automations continue.
