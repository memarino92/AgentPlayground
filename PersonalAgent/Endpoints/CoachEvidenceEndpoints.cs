using Microsoft.Extensions.Options;
using PersonalAgent.Configuration;
using PersonalAgent.Models;
using PersonalAgent.Security;
using PersonalAgent.Services;

namespace PersonalAgent.Endpoints;

internal static class CoachEvidenceEndpoints
{
    public static void MapCoachEvidence(this RouteGroupBuilder Api, IOptions<SecurityOptions> Security)
    {
        var Group = Api.MapGroup("/coach-checkins/{uploadId:guid}")
            .AddEndpointFilter(new SignedActorFilter(Security));
        Group.AddEndpointFilter(async (Context, Next) =>
        {
            Context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            Context.HttpContext.Response.Headers.XContentTypeOptions = "nosniff";
            return await Next(Context);
        });
        Group.MapGet("/evidence", async (HttpContext Context, Guid UploadId, string ProfileId, ICoachEvidenceService Service, ICoachAssignmentStore Assignments) =>
        {
            if (await PersonalAgentEndpoints.ResolveAccessAsync(Context, ProfileId, Assignments) is null) return Results.StatusCode(403);
            var Evidence = await Service.GetAsync(UploadId, ProfileId, Context.RequestAborted);
            return Evidence is null ? Results.NotFound() : Results.Ok(Evidence);
        }).WithSummary("Read a subject-authorized transcript and recording availability")
            .Produces<CoachEvidenceResponse>().Produces(403).Produces(404);
        Group.MapGet("/audio", async (HttpContext Context, Guid UploadId, string ProfileId, ICoachEvidenceService Service, ICoachAssignmentStore Assignments) =>
        {
            if (await PersonalAgentEndpoints.ResolveAccessAsync(Context, ProfileId, Assignments) is null) return Results.StatusCode(403);
            var Audio = await Service.GetAudioAsync(UploadId, ProfileId, Context.RequestAborted);
            return Audio is null ? Results.NotFound() : Results.File(Audio.Bytes, Audio.ContentType, enableRangeProcessing: true);
        }).WithSummary("Play retained original audio with authorized byte ranges")
            .Produces(200).Produces(206).Produces(403).Produces(404).Produces(416);
        Group.MapDelete("/audio", async (HttpContext Context, Guid UploadId, string ProfileId, ICoachEvidenceService Service, ICoachAssignmentStore Assignments) =>
        {
            if (SignedActorFilter.Get(Context).Role != AgentRoles.Owner
                || await PersonalAgentEndpoints.ResolveAccessAsync(Context, ProfileId, Assignments) is null) return Results.StatusCode(403);
            return await Service.DeleteAudioAsync(UploadId, ProfileId, Context.RequestAborted) switch
            {
                DeleteCoachAudioResult.Deleted => Results.NoContent(),
                DeleteCoachAudioResult.NotFound => Results.NotFound(),
                _ => Results.Conflict(new { error = "Audio can only be deleted after the call completes or fails." })
            };
        }).WithSummary("Delete a completed or failed call recording while retaining its transcript")
            .Produces(204).Produces(403).Produces(404).Produces(409);
    }
}
