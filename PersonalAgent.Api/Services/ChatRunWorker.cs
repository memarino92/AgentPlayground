using System.Diagnostics;
using System.Text;
using System.Text.Json;

using PersonalAgent.Api.Models;
using PersonalAgent.Contracts;

namespace PersonalAgent.Api.Services;

internal sealed class ChatRunWorker(ChatRunStore Store, AgentChatService Chat, ICoachAssignmentStore Assignments,
    IConversationContextStore Conversations, IAgentSessionStore Sessions, ILogger<ChatRunWorker> Logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var active = new Dictionary<Guid, (Task Task, CancellationTokenSource Cancellation)>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var (id, entry) in active.Where(pair => pair.Value.Task.IsCompleted).ToArray())
                {
                    try { await entry.Task; }
                    catch (Exception ex) { Logger.LogError(ex, "Chat run {RunId} ended unexpectedly", id); }
                    entry.Cancellation.Dispose();
                    active.Remove(id);
                }
                try
                {
                    foreach (var (id, stop) in await Store.CandidatesAsync(stoppingToken))
                    {
                        if (active.TryGetValue(id, out var running))
                        {
                            if (stop) await running.Cancellation.CancelAsync();
                            continue;
                        }
                        if (active.Count >= 4) continue;
                        var claim = await Store.TryLockAsync(id, stoppingToken);
                        if (claim is null) continue;
                        var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        active.Add(id, (ExecuteRunAsync(id, claim, cancel.Token), cancel));
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    Logger.LogError(ex, "Could not poll chat runs");
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                }
                await Task.Delay(250, stoppingToken);
            }
        }
        finally
        {
            try { await Task.WhenAll(active.Values.Select(entry => entry.Task)); }
            finally { foreach (var entry in active.Values) entry.Cancellation.Dispose(); }
        }
    }

    private async Task ExecuteRunAsync(Guid id, IAsyncDisposable claim, CancellationToken token)
    {
        await using var ownership = claim;
        var run = await Store.FindAsync(id, token);
        if (run is null || run.State is not ("queued" or "running")) return;
        // A running row without its owning connection may already have produced an external effect.
        if (run.State == "running" || run.Stop)
        {
            await FinishAsync(run, run.Stop ? "stopped" : "interrupted");
            return;
        }

        var text = new StringBuilder();
        var elapsed = new Stopwatch();
        using var writer = new SemaphoreSlim(1);
        async ValueTask FlushAsync(CancellationToken ct)
        {
            if (text.Length == 0) return;
            await Store.AppendAsync(id, JsonSerializer.Serialize(new { type = "delta", delta = text.ToString() }), ct);
            text.Clear();
            elapsed.Restart();
        }
        async ValueTask DeltaAsync(string delta, CancellationToken ct)
        {
            await writer.WaitAsync(ct);
            try
            {
                text.Append(delta);
                if (!elapsed.IsRunning || elapsed.ElapsedMilliseconds >= 100 || text.Length >= 2048) await FlushAsync(ct);
            }
            finally { writer.Release(); }
        }
        async ValueTask ActivityAsync(ChatToolActivity activity, CancellationToken ct)
        {
            await writer.WaitAsync(ct);
            try
            {
                await FlushAsync(ct);
                await Store.AppendAsync(id, JsonSerializer.Serialize(new { type = "activity", activity }, JsonSerializerOptions.Web), ct);
            }
            finally { writer.Release(); }
        }

        if (!await Store.TryStartAsync(id, token)) return;
        string state;
        try
        {
            async Task<bool> StillAssignedAsync(CancellationToken ct) =>
                (await Assignments.GetAssignedProfilesAsync(run.Access.ActorId, run.Access.Email!, ct))
                    .Contains(run.Access.SubjectProfileId, StringComparer.OrdinalIgnoreCase);
            var access = run.Access with { ToolActivity = ActivityAsync };
            if (access.Role == AgentRoles.Coach)
            {
                if (!await StillAssignedAsync(token)) throw new UnauthorizedAccessException();
                access = access with { InvocationAuthorized = StillAssignedAsync };
            }
            var reply = await Chat.SendMessageStreamingAsync(run.Session.ToString(), access,
                run.Message, DeltaAsync, token);
            state = reply is null ? "interrupted" : "completed";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            state = (await Store.FindAsync(id, CancellationToken.None))?.Stop == true ? "stopped" : "interrupted";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Chat run {RunId} failed", id);
            state = "interrupted";
        }
        await FlushAsync(CancellationToken.None);
        await FinishAsync(run, state);
    }

    private async Task FinishAsync(ChatRunStore.Run run, string state)
    {
        if (state != "completed")
        {
            await using var guard = await Conversations.LockConversationAsync(run.Session, CancellationToken.None);
            var session = await Sessions.GetSessionAsync(run.Session);
            if (session is not null && session.LastMessageSequence == run.Baseline)
            {
                var calls = new Dictionary<Guid, ChatToolActivity>();
                var reply = new StringBuilder();
                long cursor = 0;
                ChatRunStore.Page page;
                do
                {
                    page = await Store.ReadAsync(run.Id, cursor, CancellationToken.None);
                    foreach (var (seq, frame) in page.Events)
                    {
                        cursor = seq;
                        using var json = JsonDocument.Parse(frame);
                        if (json.RootElement.TryGetProperty("delta", out var delta)) reply.Append(delta.GetString());
                        if (json.RootElement.TryGetProperty("activity", out var activity) &&
                            activity.Deserialize<ChatToolActivity>(JsonSerializerOptions.Web) is { } item)
                            calls[item.CallId] = item.Status == "Running" ? item with { Status = "Interrupted" } : item;
                    }
                } while (page.Events.Count == 128);
                reply.Append(state == "stopped" ? "\n\n[Response stopped.]" : "\n\n[Response interrupted. Check tool outcomes before retrying.]");
                await Conversations.SavePresentedInteractionAsync(run.Session, run.Message, reply.ToString().Trim(),
                    session.SessionStateJson, new() { Activity = [.. calls.Values] }, CancellationToken.None);
            }
        }
        await Store.FinishAsync(run.Id, state, CancellationToken.None);
    }
}
