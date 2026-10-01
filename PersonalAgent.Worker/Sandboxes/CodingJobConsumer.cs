using System.Security.Cryptography;
using System.Text.Json;

using MassTransit;

using PersonalAgent.Contracts.Coding;
using PersonalAgent.Integrations;

namespace PersonalAgent.Worker.Sandboxes;

internal sealed class CodingJobConsumer(CodingJobStore Store, AutomationRuntimeStore Runtime, IRailwaySandboxClient Client,
    ILogger<CodingJobConsumer> Logger) : IConsumer<ExecuteCodingJob>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task Consume(ConsumeContext<ExecuteCodingJob> Context)
    {
        var Token = Context.CancellationToken;
        var Job = await Store.GetAsync(Context.Message.JobId, Token);
        if (Job is null || Job.Status != "Queued" || Job.Deadline <= DateTimeOffset.UtcNow) return;
        var Settings = await Store.SettingsAsync(Token);
        var Railway = await Runtime.ReadAsync(Token);
        if (!Settings.View.Settings.Enabled || !Railway.Settings.Enabled) return;
        var Capability = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        if (!await Store.ClaimAsync(Job.Id, Capability, Railway.Token, Railway.Settings.EnvironmentId, Token)) return;
        using var Deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Deadline.CancelAfter(TimeSpan.FromMinutes(Job.Settings.MaxMinutes));
        var Monitor = MonitorAsync(Job.Id, Deadline);
        Job = Job with { Status = "Running" };
        try
        {
            var Created = await Client.CallAsync(new { operation = "coding-create", token = Railway.Token,
                environmentId = Railway.Settings.EnvironmentId, checkpoint = Job.Settings.Checkpoint }, Deadline.Token);
            Job = Job with { SandboxId = Created.GetProperty("id").GetString(), CleanupState = "Pending" };
            // Persist identity before any candidate code runs, even if cancellation raced with provisioning.
            await Store.AttachSandboxAsync(Job.Id, Job.SandboxId!, CancellationToken.None);
            var Current = (await Store.GetAsync(Job.Id, CancellationToken.None))!;
            if (Current.Status != "Running") return;
            var Result = await Client.CallAsync(new
            {
                operation = "coding-execute", token = Railway.Token, environmentId = Railway.Settings.EnvironmentId,
                id = Job.SandboxId, imageId = Job.Settings.ImageId, repository = Job.Settings.Repository,
                baseSha = Job.BaseSha, instruction = Job.Instruction, minutes = Job.Settings.MaxMinutes,
                testProject = Job.Settings.TestProject, testFilter = Job.Settings.TestFilter,
                gateway = Railway.Settings.GatewayUrl.TrimEnd('/') + $"/coding-runtime/{Job.Id}/v1", capability = Capability
            }, Deadline.Token);
            var Artifact = Result.GetProperty("artifact").Deserialize<CodingArtifact>(Json) ?? throw new InvalidOperationException("Missing artifact.");
            var Success = Result.GetProperty("success").GetBoolean();
            Job = Job with { Artifact = Artifact, Status = Success ? "ReadyToPublish" : "Failed",
                Error = Success ? null : "Coding or validation failed. Inspect retained checks." };
            await Store.SaveAsync(Job, "Running", CancellationToken.None);
        }
        catch (Exception E)
        {
            Logger.LogError(new EventId(4403), E, "Coding execution {JobId} failed with {ExceptionType}", Job.Id, E.GetType().Name);
            await Store.SaveAsync(Job with { Status = "Failed", Error = E is OperationCanceledException ? "Coding stopped or timed out." : "Coding infrastructure failed; inspect controller diagnostics." }, "Running", CancellationToken.None);
        }
        finally
        {
            await Deadline.CancelAsync(); await Monitor;
            if (Job.SandboxId is not null) await CleanupAsync(Job.Id, Store, Client, Logger, CancellationToken.None);
        }
    }

    private async Task MonitorAsync(Guid Id, CancellationTokenSource Deadline)
    {
        try
        {
            while (!Deadline.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), Deadline.Token);
                if ((await Store.GetAsync(Id, Deadline.Token))?.Status != "Running" || !(await Store.SettingsAsync(Deadline.Token)).View.Settings.Enabled)
                    await Deadline.CancelAsync();
            }
        }
        catch (OperationCanceledException) when (Deadline.IsCancellationRequested) { }
        catch (Exception) { await Deadline.CancelAsync(); }
    }

    internal static async Task CleanupAsync(Guid Id, CodingJobStore Store, IRailwaySandboxClient Client, ILogger Logger, CancellationToken Token)
    {
        using var Timeout = CancellationTokenSource.CreateLinkedTokenSource(Token); Timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var Job = await Store.GetAsync(Id, Timeout.Token);
            if (Job?.SandboxId is null || Job.CleanupState != "Pending") return;
            var Lease = await Store.LeaseAsync(Id, Timeout.Token);
            await Client.CallAsync(new { operation = "destroy", token = Lease.Credential, environmentId = Lease.Environment, id = Job.SandboxId }, Timeout.Token);
            // Re-read so cleanup never overwrites concurrent publication or cancellation.
            await Store.ClearLeaseAsync(Id, Timeout.Token);
        }
        catch (Exception E) { Logger.LogWarning(new EventId(4404), "Coding cleanup pending for {JobId}: {ExceptionType}", Id, E.GetType().Name); }
    }
}

internal sealed class CodingSandboxReconciler(CodingJobStore Store, IRailwaySandboxClient Client, ILogger<CodingSandboxReconciler> Logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken Token) { await Store.InitializeAsync(Token); await base.StartAsync(Token); }
    protected override async Task ExecuteAsync(CancellationToken Token)
    {
        while (!Token.IsCancellationRequested)
        {
            try
            {
                foreach (var Job in await Store.PendingAsync(Token))
                    if (Job.CleanupState == "Pending" && (Job.Status != "Running" || Job.Deadline <= DateTimeOffset.UtcNow))
                        await CodingJobConsumer.CleanupAsync(Job.Id, Store, Client, Logger, Token);
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { break; }
            catch (Exception E) { Logger.LogError(new EventId(4404), E, "Coding cleanup scan failed: {ExceptionType}", E.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(15), Token);
        }
    }
}
