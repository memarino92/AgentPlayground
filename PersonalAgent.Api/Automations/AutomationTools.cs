using System.Text.Json;
using PersonalAgent.Api.Models;
using PersonalAgent.Contracts.Automations;

namespace PersonalAgent.Api.Automations;

// Chat tools are singleton-bound; create a scope per operation for the EF unit of work and bus outbox.
internal sealed class AutomationTools(IServiceScopeFactory Scopes, PersonalAgent.Integrations.ScratchpadStore Scratchpads)
{
    private static string ScratchScope(AgentAccessContext Access)
    {
        if (Access.Role != AgentRoles.Owner || Access.ScheduledTaskId is not null || !Guid.TryParse(Access.SessionId, out var Session))
            throw new UnauthorizedAccessException("Python scratchpads require an owner conversation.");
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new { Access.ActorId, Access.SubjectProfileId, Session })));
    }

    private static string PythonRecipe(string Source, string? Image = null, SandboxInputFile[]? InputFiles = null)
    {
        var Arguments = new Dictionary<string, object> { ["source"] = Source, ["input"] = "" };
        if (InputFiles is { Length: > 0 }) Arguments["inputFiles"] = InputFiles;
        if (Image is not null) Arguments["imageId"] = Image;
        return JsonSerializer.Serialize(new { steps = new[] { new { id = "python", action = "python", arguments = Arguments } } });
    }

    public Task<string> RunPythonAsync(AgentAccessContext Access, string Source, Guid RequestKey, CancellationToken Token, SandboxInputFile[]? InputFiles = null) => InvokeAsync(async Service =>
    {
        var Scope = ScratchScope(Access);
        var (Automation, Run) = await Service.StartOneShotAsync(Access, RequestKey, "Python scratchpad", PythonRecipe(Source, InputFiles: InputFiles), Token, Scope);
        var Link = $"/automations?automationId={Automation.Id}&runId={Run}&profileId={Uri.EscapeDataString(Access.SubjectProfileId)}";
        var Offset = 0;
        var Started = System.Diagnostics.Stopwatch.StartNew();
        while (Started.Elapsed < TimeSpan.FromMinutes(2))
        {
            var (Output, Status) = await Scratchpads.ProgressAsync(Run, Scope, Token);
            if (Access.ScratchOutput is { } Emit && Output.Length > Offset)
            {
                await Service.RequireScratchOutputAsync(Access, Token);
                await Emit(Output[Offset..], Token);
                Offset = Output.Length;
            }
            if (Status is "Completed" or "Failed" or "Blocked" or "Skipped")
            {
                await Service.RequireScratchOutputAsync(Access, Token);
                var Detail = await Service.RunDetailAsync(Access, Automation.Id, Run, Token);
                var Step = Detail.Steps.Single();
                if (Access.ScratchOutput is { } Flush && Step.Output is { } Final && Final.Length > Offset)
                    await Flush(Final[Offset..], Token);
                return (object)new { runId = Run, status = Status, Step.Output, Step.Error, Step.ProgramEvidence,
                    artifacts = Detail.Sandboxes?.FirstOrDefault()?.Artifacts, dashboard = Link };
            }
            await Task.Delay(200, Token);
        }
        return new { runId = Run, status = "Pending", dashboard = Link, message = "Inspect this run before retrying; reuse the same requestKey." };
    });

    public Task<string> SchedulePythonAsync(AgentAccessContext Access, Guid Run, string Name, string ExecuteAt, string RepeatEvery,
        CancellationToken Token) => InvokeAsync(async Service =>
    {
        var (Sources, Image, Files) = await Scratchpads.SourcesAsync(Run, ScratchScope(Access), Token);
        if (Sources.Count == 0) throw new ArgumentException("Choose a completed scratchpad run from this conversation.");
        if (!DateTimeOffset.TryParse(ExecuteAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var Due))
            throw new ArgumentException("Use an ISO timestamp with offset.");
        // Capture each cell as a separate compilation unit, including any future imports.
        var Code = "# scratchpad replay v1\n" + JsonSerializer.Serialize(Sources);
        var Identity = System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { Run, Name, Due = Due.ToUniversalTime(), RepeatEvery }));
        var Saved = await Service.SaveAsync(Access, null, new(Name, PythonRecipe(Code, Image, Files), Due, RepeatEvery), Token, new Guid(Identity.AsSpan(0, 16)));
        return new { automation = Saved, dashboard = $"/automations?automationId={Saved.Id}&profileId={Uri.EscapeDataString(Access.SubjectProfileId)}" };
    });

    public Task<string> RunSandboxAsync(AgentAccessContext Access, string Name, string Source, string Input, string RequestKey,
        CancellationToken Token, string[]? Packages = null, string? PackageLock = null, string[]? ReadTools = null, string Language = "csharp", SandboxInputFile[]? InputFiles = null) => InvokeAsync(async S =>
        {
            if (!Guid.TryParse(RequestKey, out var Id)) throw new ArgumentException("requestKey must be a stable UUID.");
            if (!AutomationPrograms.IsProgram(Language)) throw new ArgumentException("Choose csharp, python, shell or dockerfile.");
            var Arguments = new Dictionary<string, object?> { ["source"] = Source, ["input"] = Input };
            if (InputFiles is { Length: > 0 }) Arguments["inputFiles"] = InputFiles;
            if (Packages is { Length: > 0 }) Arguments["packages"] = Packages;
            if (PackageLock is not null) Arguments["packageLock"] = PackageLock;
            if (ReadTools is { Length: > 0 }) Arguments["tools"] = ReadTools;
            var Recipe = JsonSerializer.Serialize(new { steps = new[] { new { id = "program", action = Language, arguments = Arguments } } });
            var (Automation, RunId) = await S.StartOneShotAsync(Access, Id, Name, Recipe, Token);
            return new { automationId = Automation.Id, runId = RunId, status = "Queued",
                dashboard = $"/automations?automationId={Automation.Id}&runId={RunId}&profileId={Uri.EscapeDataString(Automation.SubjectProfileId)}",
                message = "Queued. Inspect the run for output, diagnostics and sandbox cleanup." };
        });
    public Task<string> SaveAsync(AgentAccessContext Access, string Name, string Source, Guid? AutomationId, int? ExpectedVersion,
        string? ExecuteAt, string? RepeatEvery, CancellationToken Token) => InvokeAsync(async S =>
        {
            DateTimeOffset? Due = null;
            if (!string.IsNullOrWhiteSpace(ExecuteAt))
            {
                if (!DateTimeOffset.TryParse(ExecuteAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var Parsed))
                    throw new ArgumentException("executeAt must be an ISO-8601 timestamp with offset.");
                Due = Parsed;
            }
            var Result = await S.SaveAsync(Access, AutomationId, new(Name, Source, Due, RepeatEvery, ExpectedVersion), Token);
            return new { automation = Result, dashboard = $"/automations?automationId={Result.Id}&profileId={Uri.EscapeDataString(Result.SubjectProfileId)}", message = "Saved and scheduled. Execution status is available in the dashboard; saving does not mean the run has completed." };
        });
    public Task<string> ListAsync(AgentAccessContext Access, CancellationToken Token) => InvokeAsync(async S => await S.ListAsync(Access, 0, Token));
    public Task<string> InspectAsync(AgentAccessContext Access, Guid Id, Guid? RunId, CancellationToken Token) => InvokeAsync(async S =>
        RunId is { } R ? (object)await S.RunDetailAsync(Access, Id, R, Token) : await S.DetailAsync(Access, Id, 0, Token));
    public Task<string> ControlAsync(AgentAccessContext Access, Guid Id, string Operation, CancellationToken Token) => InvokeAsync(async S =>
    {
        if (Operation == "run") return new { runId = await S.StartAsync(Id, Access, Token), status = "Queued" };
        if (Operation is not ("pause" or "resume")) throw new ArgumentException("Use run, pause, or resume.");
        await S.SetStatusAsync(Access, Id, Operation == "pause", Token);
        return (object)new { status = Operation == "pause" ? "Paused" : "Active" };
    });
    private async Task<string> InvokeAsync(Func<AutomationService, Task<object>> Operation)
    {
        await using var Scope = Scopes.CreateAsyncScope();
        try { return JsonSerializer.Serialize(await Operation(Scope.ServiceProvider.GetRequiredService<AutomationService>()), AutomationRecipes.Json); }
        catch (Exception E) when (E is ArgumentException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException)
        { return JsonSerializer.Serialize(new { error = E is KeyNotFoundException ? "Automation not found or unavailable." : E.Message }); }
    }
}
