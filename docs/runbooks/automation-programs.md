# C# automation programs

The optional `csharp` action runs a single saved C# source file as a console application. Read input from `Console.In`, write the value for subsequent steps to `Console.Out`. `System.Text.Json` and the other net11.0 BCL APIs are available. There is no package restore, application credential access, network access, or direct domain/database tool access. Code can create subprocesses only within the same bounded container. Obtain application data in an authorized read-tool step, pass it explicitly as input, and use `save_report`/`notify` after the program.

```json
{"steps":[
  {"id":"total","action":"csharp","arguments":{
    "source":"var values = Console.In.ReadToEnd().Split(',').Select(decimal.Parse); Console.Write(values.Sum());",
    "input":"10,20,30"}},
  {"id":"report","action":"save_report","arguments":{"title":"Total","content":"{{steps.total}}"}}
]}
```

Source is literal; only input supports recipe templates. Single-file source is limited to 24,000 characters within the recipe's existing 32,768-character total. Expanded input is limited to 65,536 characters. No `#:` file-based app directives are processed by the fixed project. Each scheduled occurrence rebuilds its pinned source revision in a new container. Compiler image changes take effect after runner restart and are recorded per run; no binary cache or reproducible-output guarantee is provided.

## Enable locally

```powershell
docker build -f Dockerfile.automation-sandbox -t agentplayground-csharp-sandbox:1 .
docker compose -f compose.synthetic.yml -f compose.automation-programs.yml up --build --detach --wait
```

In the existing tool access administration page, enable **C# automation programs** for Owner. Default is off. Coach access is rejected even if an administrator enables that role's entry. Ask chat to create a C# automation; the agent uses the same catalog/save/inspect tools and dashboard. Saving schedules work; only inspect the completed run to claim success. The synthetic demo's exact `demo csharp automation` fixture produces a known source file and report, not a live-model quality evaluation.

`compose.automation-programs.yml` mounts the Docker socket only into the trusted controller. It is explicitly for a disposable local/CI daemon. For hosted execution, provision a dedicated patched Linux execution host/daemon, build the sandbox image there, and run `PersonalAgent.AutomationRunner` with Docker CLI access. Use the existing `DATABASE_URL`/`CONFIG_ENCRYPTION_KEY` bootstrap and encrypted `Shared`/`AutomationRunner` database settings for messaging/telemetry, or `Messaging:ConnectionString` user secrets for local development. Do not mount the socket in the API or a program sandbox. A daemon socket grants host-level control; this is not a hostile multitenant platform. The sandbox image identity and resource policy are trusted release configuration, not agent-editable settings.

The runner fails startup if its local sandbox image is absent; it never pulls an agent-selected image. Build the image before starting it. Capability permissions are read live; image changes require a runner restart. No new environment variables are needed.

## Limits and recovery

Per invocation: 1 CPU, 512 MiB memory with swap disabled, 128 processes, 128 MiB work tmpfs, 16 MiB temporary tmpfs, 32,768 combined stdout/stderr characters, 90-second build/run deadline. Host must support cgroup resource enforcement. Two programs run concurrently per runner instance. The runtime filesystem is read-only except bounded temporary mounts; no host volumes are used. Source/input are streamed through an in-memory tar archive with fixed filenames, never interpolated into a host shell command.

The runner removes the container after success, failure, cancellation or timeout. An in-container 95-second watchdog bounds execution if the controller dies; `--rm` removes exited containers. If the Docker daemon is unavailable, inspect and remove containers labeled `personalagent.automation-sandbox=true` when it returns. Never use broad container deletion commands on a shared daemon.

Program dispatch expires after three minutes in the queue; the existing five-minute saga watchdog pauses stalled automations. Duplicate transport delivery may repeat isolated computation; terminal saga state rejects duplicate results/domain writes. Permission is checked at authoring, dispatch and result commit. Revocation while computation is running prevents successful output/domain progression but does not synchronously kill that already dispatched sandbox. Pause only stops future scheduled occurrences.

Build/runtime errors, output overflow or timeout fail the run and pause future occurrences. Source hash, image ID, exit code, elapsed time and bounded diagnostics appear under **C# build and execution** in the run's step details. Failure stdout is retained in evidence; successful stdout is the ordinary step output. These are private application data rendered as text, not telemetry. Fix source in a new revision and resume/run it; old revisions remain inspectable.

The runner emits `automation.csharp` spans and `csharp` step metrics under `PersonalAgent.Automations`. Event 4304 reports failed execution through metadata-only Sentry; logs include run/step/status/image identity, never source or stdin/stdout. Transport/infrastructure exceptions also use the existing logging integration.

## Verify

```powershell
dotnet test PersonalAgent.AutomationRunner.Tests/PersonalAgent.AutomationRunner.Tests.csproj
pwsh -NoProfile -File scripts/tests/Test-AutomationPrograms.ps1
```

Build the sandbox image first. The Docker tests include a real 90-second timeout. The smoke test verifies default-disabled enforcement, enables the synthetic owner's permission temporarily, creates a recurring C# automation through chat, checks its report/evidence and a compiler failure, then restores the original permission. It leaves paused dashboard examples. Stop the optional controller after a local trial with `docker compose -f compose.synthetic.yml -f compose.automation-programs.yml stop automation-runner`.
