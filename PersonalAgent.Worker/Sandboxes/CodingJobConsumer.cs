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
        Job = (await Store.GetAsync(Job.Id, Token))!;
        if (Job.Status != "Running") return;
        using var Deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var Remaining = (Job.AttemptDeadline ?? Job.Deadline) - DateTimeOffset.UtcNow;
        Deadline.CancelAfter(Remaining > TimeSpan.Zero ? Remaining : TimeSpan.Zero);
        var Monitor = MonitorAsync(Job.Id, Deadline);
        var phase = "sandbox provisioning";
        try
        {
            var Created = await Client.CallAsync(new { operation = "coding-create", token = Railway.Token,
                environmentId = Railway.Settings.EnvironmentId, checkpoint = Job.Settings.Checkpoint }, Deadline.Token);
            if (Created.TryGetProperty("provisioningError", out var Error))
            {
                var Reason = Error.GetString();
                Logger.LogWarning(new EventId(4403), "Coding sandbox provisioning failed for {JobId}: {Reason}", Job.Id, Reason);
                await Store.SaveAsync(Job with { Status = "Failed", Error = Reason }, "Running", CancellationToken.None);
                return;
            }
            Job = Job with { SandboxId = Created.GetProperty("id").GetString(), CleanupState = "Pending" };
            // Persist identity before any candidate code runs, even if cancellation raced with provisioning.
            phase = "saving the sandbox identity";
            await Store.AttachSandboxAsync(Job.Id, Job.SandboxId!, CancellationToken.None);
            var Current = (await Store.GetAsync(Job.Id, CancellationToken.None))!;
            if (Current.Status != "Running") return;
            Job = Current;
            phase = "sandbox execution";
            var Clarifications = Job.ResumePhase is null ? []
                : (await Store.ReadClarificationsAsync(Job.Id, Deadline.Token)).Where(Question => Question.Answer is not null).ToArray();
            var Result = await Client.CallAsync(new
            {
                operation = "coding-execute", token = Railway.Token, environmentId = Railway.Settings.EnvironmentId,
                id = Job.SandboxId, imageId = Job.Settings.ImageId, repository = Job.Settings.Repository,
                baseSha = Job.BaseSha, instruction = Job.Instruction, minutes = Job.Settings.MaxMinutes,
                executionSeconds = Math.Clamp((int)((Job.AttemptDeadline ?? Job.Deadline) - DateTimeOffset.UtcNow).TotalSeconds - 75,
                    1, Job.Settings.MaxMinutes * 60),
                supervise = Job.Supervise, resumePhase = Job.ResumePhase, resumeArtifact = JsonSerializer.SerializeToElement(ResumeArtifact(Job), Json),
                ownerClarifications = JsonSerializer.SerializeToElement(Clarifications, Json),
                canAsk = Job.SessionId is not null, followUpEvidence = Job.FollowUpEvidence, allowNoChanges = Job.AllowNoChanges,
                testProject = Job.Settings.TestProject, testFilter = Job.Settings.TestFilter,
                model = CodingJobs.TrySubscriptionModel(Job.Settings.ModelId, out _, out var Slug) ? Slug : CodingJobs.Model,
                responses = CodingJobs.IsSubscriptionModel(Job.Settings.ModelId),
                gateway = Railway.Settings.GatewayUrl.TrimEnd('/') + $"/coding-runtime/{Job.Id}/v1", capability = Capability
            }, Deadline.Token);
            phase = "reading validation results";
            var Artifact = Result.GetProperty("artifact").Deserialize<CodingArtifact>(Json) ?? throw new InvalidOperationException("Missing artifact.");
            var ResumePhase = Result.TryGetProperty("resumePhase", out var NextPhase) ? NextPhase.GetString() : null;
            if (ResumePhase is "code" or "validate" && CanSaveCheckpoint(Job, Artifact, ResumePhase))
            {
                var Latest = (await Store.GetAsync(Job.Id, CancellationToken.None))!;
                var HasProgress = Result.TryGetProperty("hasProgress", out var Progress) && Progress.ValueKind == JsonValueKind.True;
                if (!HasProgress && Job.SessionId is not null)
                    HasProgress = (await Store.ReadClarificationsAsync(Job.Id, CancellationToken.None)).Count > 0;
                var CanContinue = HasProgress && Latest.Attempt < Latest.AttemptLimit
                    && (ResumePhase == "validate" || Latest.ModelRequests < Latest.Settings.MaxModelRequests)
                    && Latest.Deadline > DateTimeOffset.UtcNow.AddMinutes(2);
                await Store.SaveAsync(Job with
                {
                    Artifact = Artifact, ResumePhase = CanContinue ? ResumePhase : null,
                    Status = CanContinue ? CodingJobs.Resuming : "Failed",
                    Error = CanContinue ? null : "The sandbox reached its time limit. The last checkpoint is available in this job."
                }, "Running", CancellationToken.None);
                return;
            }
            var VerifiedEmpty = Artifact.BaseSha == Job.BaseSha && Artifact.Files is { Length: 0 }
                && Artifact.Checks is { Length: 3 } && Artifact.Checks.All(Check => Check.ExitCode == 0);
            var Success = Result.GetProperty("success").GetBoolean()
                && (Artifact.Files.Length > 0 || (Job.AllowNoChanges && VerifiedEmpty));
            var Status = !Success ? "Failed" : VerifiedEmpty ? CodingJobs.CompletedNoChanges : "ReadyToPublish";
            Job = Job with
            {
                Artifact = Artifact.Files is { Length: 0 } && !Success ? Job.Artifact ?? Artifact : Artifact,
                ResumePhase = null,
                Status = Status,
                Error = Success ? null : VerifiedEmpty && !Job.AllowNoChanges ? "No file changes were found. This job expected a diff."
                    : "Coding or validation failed. Inspect retained checks."
            };
            phase = "saving validation results";
            await Store.SaveAsync(Job, "Running", CancellationToken.None);
        }
        catch (Exception E)
        {
            Logger.LogError(new EventId(4403), E, "Coding execution {JobId} failed with {ExceptionType}", Job.Id, E.GetType().Name);
            await Store.SaveAsync(Job with { Status = "Failed", Error = E is OperationCanceledException
                ? $"Coding stopped or timed out during {phase}." : $"Coding failed during {phase} ({E.GetType().Name})." }, "Running", CancellationToken.None);
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
            await Store.ClearLeaseAsync(Id, Job.SandboxId, Timeout.Token);
        }
        catch (Exception E) { Logger.LogWarning(new EventId(4404), "Coding cleanup pending for {JobId}: {ExceptionType}", Id, E.GetType().Name); }
    }

    private static CodingArtifact? ResumeArtifact(CodingJob Job) =>
        Job.ResumePhase is null || Job.Artifact is not { } Artifact ? null : Artifact with
        {
            Checks = Job.ResumePhase == "validate"
                ? Artifact.Checks.Where(Check => Check is { Command: "code", ExitCode: 0 }).Select(Check => Check with { Output = "" }).ToArray()
                : []
        };

    private static bool CanSaveCheckpoint(CodingJob Job, CodingArtifact Artifact, string ResumePhase) =>
        Artifact.BaseSha == Job.BaseSha && CodingArtifactFiles.AreAllowed(Artifact.Files)
        && Artifact.Summary is { Length: <= 4000 } && Artifact.Checks is { Length: <= 4 }
        && Artifact.Checks.All(Check => Check is not null && Check.Output is { Length: <= 20000 })
        && (ResumePhase == "code" || Artifact.Checks.Count(Check => Check is { Command: "code", ExitCode: 0 }) == 1)
        && JsonSerializer.SerializeToUtf8Bytes(Artifact).Length <= CodingJobs.MaxArtifactBytes;
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
                    if (Job.CleanupState == "Pending" && (Job.Status != "Running" || (Job.AttemptDeadline ?? Job.Deadline) <= DateTimeOffset.UtcNow))
                        await CodingJobConsumer.CleanupAsync(Job.Id, Store, Client, Logger, Token);
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { break; }
            catch (Exception E) { Logger.LogError(new EventId(4404), E, "Coding cleanup scan failed: {ExceptionType}", E.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(15), Token);
        }
    }
}
