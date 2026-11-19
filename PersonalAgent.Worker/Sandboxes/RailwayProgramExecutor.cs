using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PersonalAgent.Contracts.Automations;
using PersonalAgent.Integrations;

namespace PersonalAgent.Worker.Sandboxes;

public interface IProgramExecutor
{
    Task<ProgramResult> ExecuteAsync(ExecuteAutomationProgram Message, CancellationToken Token);
}

public interface IRailwaySandboxClient
{
    Task<JsonElement> CallAsync(object Request, CancellationToken Token);
}

public sealed class RailwaySandboxClient : IRailwaySandboxClient
{
    public async Task<JsonElement> CallAsync(object Request, CancellationToken Token)
    {
        using var Process = new Process { StartInfo = new("node") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        Process.StartInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Railway", "bridge.mjs"));
        // The bridge needs only the JSON request; prevent accidental inherited credentials/SDK defaults.
        var PathValue = Process.StartInfo.Environment["PATH"];
        Process.StartInfo.Environment.Clear();
        Process.StartInfo.Environment["PATH"] = PathValue;
        if (!Process.Start()) throw new InvalidOperationException("Sandbox adapter did not start.");
        async Task<string> ReadAsync(StreamReader Reader)
        {
            var Text = new StringBuilder(); var Buffer = new char[2048]; int Count;
            while ((Count = await Reader.ReadAsync(Buffer.AsMemory(), Token)) > 0)
            {
                if (Text.Length + Count > 2000000) throw new InvalidOperationException("Sandbox adapter response limit.");
                Text.Append(Buffer, 0, Count);
            }
            return Text.ToString();
        }
        var Output = ReadAsync(Process.StandardOutput); var Error = ReadAsync(Process.StandardError);
        try
        {
            await Process.StandardInput.WriteAsync(JsonSerializer.Serialize(Request).AsMemory(), Token);
            Process.StandardInput.Close();
            await Process.WaitForExitAsync(Token);
            await Task.WhenAll(Output, Error);
            if (Process.ExitCode != 0) throw new InvalidOperationException("Railway sandbox operation failed.");
            return JsonSerializer.Deserialize<JsonElement>(await Output);
        }
        finally
        {
            if (!Process.HasExited) Process.Kill(entireProcessTree: true);
            try { await Task.WhenAll(Output, Error); } catch (Exception) { /* Observe bounded reader cancellation/failure. */ }
        }
    }
}

public sealed class RailwayProgramExecutor(AutomationRuntimeStore Runtime, AutomationSandboxStore Leases,
    IRailwaySandboxClient Client, ILogger<RailwayProgramExecutor> Logger, ScratchpadStore? Scratchpads = null, SandboxInputStore? Inputs = null) : IProgramExecutor
{
    public async Task<ProgramResult> ExecuteAsync(ExecuteAutomationProgram Message, CancellationToken Token)
    {
        var Started = Stopwatch.GetTimestamp();
        var Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Message.Source)));
        var Snapshot = await Runtime.ReadAsync(Token);
        var Settings = Snapshot.Settings;
        if (!Settings.Enabled) return new("", new(Hash, "", null, "Disabled", "Configure Railway execution in Settings → Automation runner.", 0));
        AutomationRuntimeStore.Validate(Settings, Snapshot.Token);
        if (Message.RequiredImageId is not null && Message.RequiredImageId != Settings.ImageId)
            return new("", new(Hash, Settings.ImageId, null, "ImageChanged", "The saved Python calculation requires its original checkpoint image.", 0));
        var CustomImage = Message.Language == "dockerfile";
        var Capability = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var CapabilityHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Capability)));
        if (!await Leases.ClaimAsync(Message.RunId, Message.StepIndex, Snapshot, CapabilityHash, Token, CustomImage))
        {
            var Saved = await Leases.ResultAsync(Message.RunId, Message.StepIndex, Token);
            return Saved is not null ? JsonSerializer.Deserialize<ProgramResult>(Saved)!
                : throw new InvalidOperationException("Program already claimed; execution will not be replayed.");
        }
        var Scope = Message.Language == "python" && Scratchpads is not null ? await Scratchpads.ScopeAsync(Message.RunId, Token) : null;
        await using var Guard = Scope is null ? null : await Scratchpads!.LockAsync(Scope, Token);
        string? Id = null;
        ProgramResult Result;
        SandboxArtifactBundle? Bundle = null;
        var TransferPath = Path.Combine(Path.GetTempPath(), $"sandbox-export-{Guid.NewGuid():N}.tar");
        using var Deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Deadline.CancelAfter(TimeSpan.FromMinutes(CustomImage ? 15 : 3));
        try
        {
            var Files = Message.InputFiles ?? [];
            SandboxInputFile.Validate(Files);
            var Remaining = SandboxInputFile.MaxBytes;
            var Staged = new List<object>();
            foreach (var File in Files)
            {
                var Bytes = await Inputs!.ReadAsync(Message.RunId, File, Remaining, Deadline.Token);
                Remaining -= Bytes.Length;
                var Path = TransferPath + ".input-" + Staged.Count;
                await System.IO.File.WriteAllBytesAsync(Path, Bytes, Deadline.Token);
                Staged.Add(new { name = File.Name, path = Path });
            }
            var Dependencies = Message.Packages is { Length: > 0 }
                ? AutomationPackageLock.ResolvedPackages(Message.PackageLock ?? throw new ArgumentException("Package lock required.")) : [];
            if ((Message.Packages ?? []).Concat(Dependencies).Any(P => !Settings.ApprovedPackages.Contains(P, StringComparer.OrdinalIgnoreCase)))
                throw new UnauthorizedAccessException();
            using (AutomationTelemetry.Start("automation.sandbox.create", Message.RunId, Message.StepIndex))
            {
                var Previous = Scope is null ? null : await Scratchpads!.BeginAsync(Message.RunId, Scope, Snapshot.Revision, Deadline.Token);
                var Created = await Client.CallAsync(new { operation = Scope is null ? "create" : "scratch-open", previousId = Previous,
                    customImage = CustomImage, token = Snapshot.Token, checkpoint = Settings.Checkpoint, imageId = Settings.ImageId, environmentId = Settings.EnvironmentId }, Deadline.Token);
                Id = Created.GetProperty("id").GetString() ?? throw new InvalidOperationException("Missing sandbox identity.");
                // No untrusted code starts before this durable identity exists for the cleanup worker.
                await Leases.AttachAsync(Message.RunId, Message.StepIndex, Id, Deadline.Token);
            }
            var Execution = Client.CallAsync(new { operation = Scope is null ? "execute" : "scratch-cell", progressPath = TransferPath + ".progress", token = Snapshot.Token, environmentId = Settings.EnvironmentId, id = Id, imageId = Settings.ImageId,
                inputFiles = Staged, artifactDestination = TransferPath, language = Message.Language, source = Message.Source, input = Message.Input, packages = Message.Packages, packageLock = Message.PackageLock,
                gatewayUrl = Settings.GatewayUrl.TrimEnd('/') + $"/automation-runtime/{Message.RunId}/{Message.StepIndex}/tools",
                gatewayToken = Capability }, Deadline.Token);
            if (Scope is not null)
            {
                var Last = "";
                try
                {
                    while (await Task.WhenAny(Execution, Task.Delay(200, Deadline.Token)) != Execution)
                    {
                        Deadline.Token.ThrowIfCancellationRequested();
                        if (!File.Exists(TransferPath + ".progress")) continue;
                        var Current = (await File.ReadAllTextAsync(TransferPath + ".progress", Deadline.Token)).TrimEnd('\uFFFD');
                        if (Current == Last) continue;
                        await Scratchpads!.WriteProgressAsync(Message.RunId, Current, Deadline.Token);
                        Last = Current;
                    }
                }
                catch { await Deadline.CancelAsync(); try { await Execution; } catch (Exception) { } throw; }
            }
            var Response = await Execution;
            var Status = Response.GetProperty("status").GetString()!;
            var Output = Response.GetProperty("stdout").GetString() ?? "";
            if (Status == "Completed" && Response.TryGetProperty("hasArtifacts", out var Export) && Export.GetBoolean())
                Bundle = await SandboxArtifactBundle.ReadAsync(TransferPath, Deadline.Token);
            Result = new(Status == "Completed" ? Output : "", new(Hash, CustomImage ? (Response.TryGetProperty("imageId", out var Image) ? Image.GetString() ?? "" : "") : Settings.ImageId,
                Response.GetProperty("exitCode").ValueKind == JsonValueKind.Number ? Response.GetProperty("exitCode").GetInt32() : null,
                Status, Response.GetProperty("stderr").GetString() ?? "", Stopwatch.GetElapsedTime(Started).TotalSeconds, Status == "Completed" ? "" : Output, Id));
        }
        catch (KeyNotFoundException)
        {
            Result = new("", new(Hash, "", null, "InputUnavailable", "Check the file reference, actor, profile and 16 MiB total size.", Stopwatch.GetElapsedTime(Started).TotalSeconds));
        }
        catch (InvalidDataException)
        {
            Result = new("", new(Hash, "", null, "ArtifactExportFailed",
                "Invalid artifact archive. Use portable relative paths, at most 512 files and 64 MiB of content.", Stopwatch.GetElapsedTime(Started).TotalSeconds, SandboxId: Id));
        }
        catch (Exception Exception)
        {
            Logger.LogError(new EventId(4305), Exception, "Sandbox execution {RunId}/{StepIndex} failed with {ExceptionType}", Message.RunId, Message.StepIndex, Exception.GetType().Name);
            Result = new("", new(Hash, CustomImage ? "" : Settings.ImageId, null, Exception is OperationCanceledException ? "TimedOut" : "InfrastructureFailed",
                "Sandbox failed; inspect correlated controller logs. Cleanup is retried durably.", Stopwatch.GetElapsedTime(Started).TotalSeconds, SandboxId: Id));
        }
        finally
        {
            try
            {
                File.Delete(TransferPath);
                File.Delete(TransferPath + ".progress");
                for (var I = 0; I < (Message.InputFiles?.Length ?? 0); I++) File.Delete(TransferPath + ".input-" + I);
            }
            catch (IOException E) { Logger.LogWarning(E, "Temporary artifact cleanup failed for {RunId}", Message.RunId); }
        }
        using var Finish = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var KeepWarm = Scope is not null && Id is not null && Result.Evidence.Status is "Completed" or "PythonError";
        await Leases.SaveResultAsync(Message.RunId, Message.StepIndex, JsonSerializer.Serialize(Result), Finish.Token, Bundle?.Zip, Bundle?.Files, KeepWarm);
        if (!KeepWarm && Id is not null)
        {
            try
            {
                await Client.CallAsync(new { operation = "destroy", token = Snapshot.Token, environmentId = Settings.EnvironmentId, id = Id }, Finish.Token);
                await Leases.DestroyedAsync(Message.RunId, Message.StepIndex, Finish.Token);
            }
            catch (Exception Exception) { Logger.LogError(new EventId(4306), Exception, "Sandbox cleanup pending for {RunId}/{StepIndex}", Message.RunId, Message.StepIndex); }
        }
        return Result;
    }
}

internal sealed class SandboxReconciler(AutomationRuntimeStore Runtime, AutomationSandboxStore Leases,
    IRailwaySandboxClient Client, ILogger<SandboxReconciler> Logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken Token) { await Runtime.InitializeAsync(Token); await base.StartAsync(Token); }
    protected override async Task ExecuteAsync(CancellationToken StoppingToken)
    {
        using var Timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                foreach (var Lease in await Leases.CleanupAsync(StoppingToken))
                {
                    using var Deadline = CancellationTokenSource.CreateLinkedTokenSource(StoppingToken);
                    Deadline.CancelAfter(TimeSpan.FromSeconds(20));
                    using var Span = AutomationTelemetry.Start("automation.sandbox.cleanup", Lease.RunId, Lease.Step);
                    try
                    {
                        if (Lease.SandboxId is not null) await Client.CallAsync(new { operation = "destroy", token = Lease.Credential, environmentId = Lease.EnvironmentId, id = Lease.SandboxId }, Deadline.Token);
                        await Leases.DestroyedAsync(Lease.RunId, Lease.Step, Deadline.Token);
                        Logger.LogInformation("Sandbox lease cleaned for {RunId}/{StepIndex}", Lease.RunId, Lease.Step);
                    }
                    catch (OperationCanceledException) when (StoppingToken.IsCancellationRequested) { return; }
                    catch (Exception Exception) { Logger.LogError(new EventId(4306), Exception, "Sandbox cleanup pending for {RunId}/{StepIndex}", Lease.RunId, Lease.Step); }
                }
            }
            catch (OperationCanceledException) when (StoppingToken.IsCancellationRequested) { return; }
            catch (Exception Exception) { Logger.LogError(new EventId(4306), Exception, "Sandbox reconciliation failed; retrying on next poll."); }
        } while (await Timer.WaitForNextTickAsync(StoppingToken));
    }
}
