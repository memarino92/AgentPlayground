using AgentPlayground.Contracts.Messaging.Commands;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using PersonalAgent.Configuration;
using PersonalAgent.Models;
using PersonalAgent.Security;
using PersonalAgent.Services;

namespace PersonalAgent.Endpoints;

internal static class ScheduledJobEndpoints
{
    public static void MapScheduledJobs(this RouteGroupBuilder Api, IOptions<SecurityOptions> Security)
    {
        // Authority and executable payload come from storage, never from the delivery.
        Api.MapPost("/jobs/{id:guid}/execute", async (Guid id, ExecuteAgentTask delivery, [FromServices] ScheduledJobExecutionService execution, CancellationToken token) =>
        {
            if (id != delivery.TaskId) return Results.BadRequest();
            return Results.Ok(await execution.ExecuteAsync(delivery, token));
        });
        var Group = Api.MapGroup("/jobs").AddEndpointFilter(new SignedActorFilter(Security));
        Group.MapGet("/", async (HttpContext context, string profileId, string? status, DateTimeOffset? before,
            [FromServices] ScheduledJobAuthorization authorization, [FromServices] ScheduledJobStore store, CancellationToken token) =>
        {
            var Actor = SignedActorFilter.Get(context);
            var Access = await authorization.ResolveAsync(Actor.ActorId, Actor.Email, profileId, token);
            if (Access is null || Access.Role != Actor.Role) return Results.StatusCode(403);
            var Jobs = await store.ListAsync(profileId, Access.Role == AgentRoles.Coach ? Actor.ActorId : null, status, before, token);
            return Results.Ok(Jobs.Select(Job => Visible(Job, Actor.ActorId)));
        });
        Group.MapGet("/{id:guid}", async (HttpContext context, Guid id, [FromServices] ScheduledJobAuthorization authorization, [FromServices] ScheduledJobStore store, CancellationToken token) =>
        {
            var Job = await AccessibleAsync(context, id, authorization, store, token);
            return Job is null ? Results.NotFound() : Results.Ok(new ScheduledJobDetail(Visible(Job, SignedActorFilter.Get(context).ActorId), await store.GetAttemptsAsync(id, token)));
        });
        Group.MapPost("/{id:guid}/cancel", async (HttpContext context, Guid id, [FromServices] ScheduledJobAuthorization authorization,
            [FromServices] ScheduledJobStore store, [FromServices] ScheduledJobExecutionService execution, CancellationToken token) =>
        {
            if (await AccessibleAsync(context, id, authorization, store, token) is null) return Results.NotFound();
            return await execution.CancelAsync(id, token) ? Results.NoContent() : Results.Conflict(new { error = "Only pending jobs can be cancelled." });
        });
    }

    private static ScheduledJob Visible(ScheduledJob Job, string ActorId) => Job.ActorId == ActorId
        ? Job : Job with { SourceSessionId = null, SessionId = null };

    private static async Task<ScheduledJob?> AccessibleAsync(HttpContext Context, Guid Id, ScheduledJobAuthorization Authorization, ScheduledJobStore Store, CancellationToken Token)
    {
        var Job = await Store.GetAsync(Id, Token);
        if (Job is null) return null;
        var Actor = SignedActorFilter.Get(Context);
        var Access = await Authorization.ResolveAsync(Actor.ActorId, Actor.Email, Job.SubjectProfileId, Token);
        return Access is not null && Access.Role == Actor.Role && (Access.Role == AgentRoles.Owner || Job.ActorId == Actor.ActorId) ? Job : null;
    }
}
