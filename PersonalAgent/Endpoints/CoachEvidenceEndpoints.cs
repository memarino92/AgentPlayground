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
        Group.MapPut("/audio", async (HttpContext Context, Guid UploadId, string ProfileId, ICoachEvidenceService Service, ICoachAssignmentStore Assignments, IOptions<CoachCheckinOptions> UploadOptions) =>
        {
            if (SignedActorFilter.Get(Context).Role != AgentRoles.Owner
                || await PersonalAgentEndpoints.ResolveAccessAsync(Context, ProfileId, Assignments) is null) return Results.StatusCode(403);
            var Limit = UploadOptions.Value.MaxUploadMb * 1024L * 1024L;
            if (Context.Request.ContentLength > Limit) return Results.StatusCode(413);
            await using var Buffer = new MemoryStream();
            var Block = new byte[81920];
            int Read;
            while ((Read = await Context.Request.Body.ReadAsync(Block, Context.RequestAborted)) > 0)
            {
                if (Buffer.Length + Read > Limit) return Results.StatusCode(413);
                await Buffer.WriteAsync(Block.AsMemory(0, Read), Context.RequestAborted);
            }
            if (Buffer.Length == 0) return Results.BadRequest(new { error = "Choose an audio file to upload." });
            return await Service.AttachAudioAsync(UploadId, ProfileId, Buffer.ToArray(), Context.Request.ContentType ?? "application/octet-stream", Context.RequestAborted) switch
            {
                AttachCoachAudioResult.Stored => Results.NoContent(),
                AttachCoachAudioResult.NotFound => Results.NotFound(),
                _ => Results.Conflict(new { error = "Audio can only be attached to a completed call." })
            };
        }).WithSummary("Attach Owner-selected audio to a completed call without verifying or reprocessing it")
            .Produces(204).Produces(400).Produces(403).Produces(404).Produces(409).Produces(413);
    }
}
