using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PersonalAgent.Contracts.Automations;
using PersonalAgent.Integrations;

namespace PersonalAgent.AutomationRunner;

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
                if (Text.Length + Count > 150000) throw new InvalidOperationException("Sandbox adapter response limit.");
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
    IRailwaySandboxClient Client, ILogger<RailwayProgramExecutor> Logger) : IProgramExecutor
{
    public async Task<ProgramResult> ExecuteAsync(ExecuteAutomationProgram Message, CancellationToken Token)
    {
        var Started = Stopwatch.GetTimestamp();
        var Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Message.Source)));
        var Snapshot = await Runtime.ReadAsync(Token);
        var Settings = Snapshot.Settings;
        if (!Settings.Enabled) return new("", new(Hash, "", null, "Disabled", "Configure Railway execution in Settings → Automation runner.", 0));
        AutomationRuntimeStore.Validate(Settings, Snapshot.Token);
        var Capability = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var CapabilityHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Capability)));
        if (!await Leases.ClaimAsync(Message.RunId, Message.StepIndex, Snapshot, CapabilityHash, Token))
        {
            var Saved = await Leases.ResultAsync(Message.RunId, Message.StepIndex, Token);
            return Saved is not null ? JsonSerializer.Deserialize<ProgramResult>(Saved)!
                : throw new InvalidOperationException("Program already claimed; execution will not be replayed.");
        }
        string? Id = null;
        ProgramResult Result;
        using var Deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Deadline.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            var Dependencies = Message.Packages is { Length: > 0 }
                ? AutomationPackageLock.ResolvedPackages(Message.PackageLock ?? throw new ArgumentException("Package lock required.")) : [];
            if ((Message.Packages ?? []).Concat(Dependencies).Any(P => !Settings.ApprovedPackages.Contains(P, StringComparer.OrdinalIgnoreCase)))
                throw new UnauthorizedAccessException();
            using (AutomationTelemetry.Start("automation.sandbox.create", Message.RunId, Message.StepIndex))
            {
                var Created = await Client.CallAsync(new { operation = "create", token = Snapshot.Token, checkpoint = Settings.Checkpoint, environmentId = Settings.EnvironmentId }, Deadline.Token);
                Id = Created.GetProperty("id").GetString() ?? throw new InvalidOperationException("Missing sandbox identity.");
                // No untrusted code starts before this durable identity exists for the cleanup worker.
                await Leases.AttachAsync(Message.RunId, Message.StepIndex, Id, Deadline.Token);
            }
            var Response = await Client.CallAsync(new { operation = "execute", token = Snapshot.Token, environmentId = Settings.EnvironmentId, id = Id, imageId = Settings.ImageId,
                source = Message.Source, input = Message.Input, packages = Message.Packages, packageLock = Message.PackageLock,
                gatewayUrl = Settings.GatewayUrl.TrimEnd('/') + $"/automation-runtime/{Message.RunId}/{Message.StepIndex}/tools",
                gatewayToken = Capability }, Deadline.Token);
            var Status = Response.GetProperty("status").GetString()!;
            var Output = Response.GetProperty("stdout").GetString() ?? "";
            Result = new(Status == "Completed" ? Output : "", new(Hash, Settings.ImageId,
                Response.GetProperty("exitCode").ValueKind == JsonValueKind.Number ? Response.GetProperty("exitCode").GetInt32() : null,
                Status, Response.GetProperty("stderr").GetString() ?? "", Stopwatch.GetElapsedTime(Started).TotalSeconds, Status == "Completed" ? "" : Output, Id));
        }
        catch (Exception Exception)
        {
            Logger.LogError(new EventId(4305), "Sandbox execution {RunId}/{StepIndex} failed with {ExceptionType}", Message.RunId, Message.StepIndex, Exception.GetType().Name);
            Result = new("", new(Hash, Settings.ImageId, null, Exception is OperationCanceledException ? "TimedOut" : "InfrastructureFailed",
                "Sandbox failed; inspect correlated controller logs. Cleanup is retried durably.", Stopwatch.GetElapsedTime(Started).TotalSeconds, SandboxId: Id));
        }
        using var Finish = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Leases.SaveResultAsync(Message.RunId, Message.StepIndex, JsonSerializer.Serialize(Result), Finish.Token);
        if (Id is not null)
        {
            try
            {
                await Client.CallAsync(new { operation = "destroy", token = Snapshot.Token, environmentId = Settings.EnvironmentId, id = Id }, Finish.Token);
                await Leases.DestroyedAsync(Message.RunId, Message.StepIndex, Finish.Token);
            }
            catch (Exception) { Logger.LogError(new EventId(4306), "Sandbox cleanup pending for {RunId}/{StepIndex}", Message.RunId, Message.StepIndex); }
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
                    catch (Exception) { Logger.LogError(new EventId(4306), "Sandbox cleanup pending for {RunId}/{StepIndex}", Lease.RunId, Lease.Step); }
                }
            }
            catch (OperationCanceledException) when (StoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { Logger.LogError(new EventId(4306), "Sandbox reconciliation failed; retrying on next poll."); }
        } while (await Timer.WaitForNextTickAsync(StoppingToken));
    }
}
