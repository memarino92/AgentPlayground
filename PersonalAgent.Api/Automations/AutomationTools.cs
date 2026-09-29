using System.Text.Json;
using PersonalAgent.Api.Models;
using PersonalAgent.Contracts.Automations;

namespace PersonalAgent.Api.Automations;

// Chat tools are singleton-bound; create a scope per operation for the EF unit of work and bus outbox.
internal sealed class AutomationTools(IServiceScopeFactory Scopes)
{
    public Task<string> RunSandboxAsync(AgentAccessContext Access, string Name, string Source, string Input, string RequestKey,
        CancellationToken Token, string[]? Packages = null, string? PackageLock = null, string[]? ReadTools = null) => InvokeAsync(async S =>
        {
            if (!Guid.TryParse(RequestKey, out var Id)) throw new ArgumentException("requestKey must be a stable UUID.");
            var Arguments = new Dictionary<string, object?> { ["source"] = Source, ["input"] = Input };
            if (Packages is { Length: > 0 }) Arguments["packages"] = Packages;
            if (PackageLock is not null) Arguments["packageLock"] = PackageLock;
            if (ReadTools is { Length: > 0 }) Arguments["tools"] = ReadTools;
            var Recipe = JsonSerializer.Serialize(new { steps = new[] { new { id = "program", action = "csharp", arguments = Arguments } } });
            var (Automation, RunId) = await S.StartOneShotAsync(Access, Id, Name, Recipe, Token);
            return new { automationId = Automation.Id, runId = RunId, status = "Queued",
                dashboard = $"/automations?automationId={Automation.Id}&profileId={Uri.EscapeDataString(Automation.SubjectProfileId)}",
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
