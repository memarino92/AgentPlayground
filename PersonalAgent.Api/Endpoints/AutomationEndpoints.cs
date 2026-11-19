using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PersonalAgent.Api.Automations;
using PersonalAgent.Api.Configuration;
using PersonalAgent.Api.Models;
using PersonalAgent.Api.Security;
using PersonalAgent.Contracts.Automations;

namespace PersonalAgent.Api.Endpoints;

internal static class AutomationEndpoints
{
    public static void MapAutomations(this RouteGroupBuilder Api, IOptions<SecurityOptions> Security)
    {
        var Group = Api.MapGroup("/automations").AddEndpointFilter(new SignedActorFilter(Security));
        Group.AddEndpointFilter(async (Context, Next) =>
        {
            try { return await Next(Context); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException E) { return Results.BadRequest(new { error = E.Message }); }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "The automation changed or already has an active run. Refresh and try again." }); }
        });
        Group.MapPost("/inputs", async (HttpContext C, string profileId, [FromServices] AutomationAuthorization Authorization,
            [FromServices] PersonalAgent.Integrations.SandboxInputStore Store, CancellationToken Token) =>
        {
            var Actor = await Authorization.RequireAsync(Access(C, profileId), Token, Write: false);
            if (Actor.Role != AgentRoles.Owner) return Results.StatusCode(403);
            if (C.Request.ContentLength is not long Size || Size < 0 || Size > SandboxInputFile.MaxBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var Bytes = new byte[(int)Size];
            await C.Request.Body.ReadExactlyAsync(Bytes, Token);
            return Results.Ok(await Store.UploadAsync(Actor.ActorId, Actor.SubjectProfileId, Bytes, Token));
        });
        Group.MapGet("/", async (HttpContext C, string profileId, [FromServices] AutomationService Service, CancellationToken Token, int offset = 0) =>
            TypedResults.Ok(await Service.ListAsync(Access(C, profileId), offset, Token)));
        Group.MapGet("/{id:guid}", async (HttpContext C, Guid id, string profileId, [FromServices] AutomationService Service, CancellationToken Token, int runOffset = 0) =>
            TypedResults.Ok(await Service.DetailAsync(Access(C, profileId), id, runOffset, Token)));
        Group.MapGet("/{id:guid}/runs/{runId:guid}", async (HttpContext C, Guid id, Guid runId, string profileId, [FromServices] AutomationService Service, CancellationToken Token) =>
            TypedResults.Ok(await Service.RunDetailAsync(Access(C, profileId), id, runId, Token)));
        Group.MapGet("/{id:guid}/runs/{runId:guid}/artifacts/{step:int}", async (HttpContext C, Guid id, Guid runId, int step,
            string profileId, int? file, bool? preview, [FromServices] AutomationService Service, CancellationToken Token) =>
        {
            var Bytes = await Service.ReadArtifactArchiveAsync(Access(C, profileId), id, runId, step, Token);
            C.Response.Headers.CacheControl = "private, no-store";
            C.Response.Headers.XContentTypeOptions = "nosniff";
            if (file is null) return Results.File(Bytes, "application/zip", $"artifacts-{runId}-step-{step + 1}.zip");
            using var Zip = new System.IO.Compression.ZipArchive(new MemoryStream(Bytes));
            var Member = Zip.Entries.Where(E => E.Name.Length > 0).ElementAtOrDefault(file.Value);
            if (Member is null) return Results.NotFound();
            var InlinePlot = preview == true && Member.FullName == "plot.png";
            if (InlinePlot)
            {
                using var Check = Member.Open();
                var Signature = new byte[8];
                if (Member.Length < 8) return Results.NotFound();
                await Check.ReadExactlyAsync(Signature, Token);
                if (Convert.ToHexString(Signature) != "89504E470D0A1A0A") return Results.NotFound();
                C.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            }
            var Name = Member.FullName;
            C.Response.ContentLength = Member.Length;
            return Results.Stream(async Destination =>
            {
                using var Archive = new System.IO.Compression.ZipArchive(new MemoryStream(Bytes));
                using var Source = Archive.GetEntry(Name)!.Open();
                await Source.CopyToAsync(Destination, Token);
            }, InlinePlot ? "image/png" : "application/octet-stream", InlinePlot ? null : Member.Name);
        });
        Group.MapPost("/", async (HttpContext C, string profileId, SaveAutomationRequest Request, [FromServices] AutomationService Service, CancellationToken Token) =>
        {
            var Saved = await Service.SaveAsync(Access(C, profileId), null, Request, Token);
            return TypedResults.Created($"/api/automations/{Saved.Id}?profileId={Uri.EscapeDataString(profileId)}", Saved);
        });
        Group.MapPut("/{id:guid}", async (HttpContext C, Guid id, string profileId, SaveAutomationRequest Request, [FromServices] AutomationService Service, CancellationToken Token) =>
            TypedResults.Ok(await Service.SaveAsync(Access(C, profileId), id, Request, Token)));
        Group.MapPost("/{id:guid}/{operation}", async (HttpContext C, Guid id, string operation, string profileId, [FromServices] AutomationService Service, CancellationToken Token) =>
        {
            var Actor = Access(C, profileId);
            if (operation == "run") return Results.Accepted(value: new { runId = await Service.StartAsync(id, Actor, Token) });
            if (operation is not ("pause" or "resume")) return Results.BadRequest();
            await Service.SetStatusAsync(Actor, id, operation == "pause", Token);
            return Results.NoContent();
        });
    }

    private static AgentAccessContext Access(HttpContext Context, string ProfileId)
    {
        var Actor = SignedActorFilter.Get(Context);
        return new(Actor.ActorId, Actor.Role, ProfileId) { Email = Actor.Email };
    }
}
