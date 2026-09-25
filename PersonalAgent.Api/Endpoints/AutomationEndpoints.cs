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
        Group.MapGet("/", async (HttpContext C, string profileId, [FromServices] AutomationService Service, CancellationToken Token, int offset = 0) =>
            TypedResults.Ok(await Service.ListAsync(Access(C, profileId), offset, Token)));
        Group.MapGet("/{id:guid}", async (HttpContext C, Guid id, string profileId, [FromServices] AutomationService Service, CancellationToken Token, int runOffset = 0) =>
            TypedResults.Ok(await Service.DetailAsync(Access(C, profileId), id, runOffset, Token)));
        Group.MapGet("/{id:guid}/runs/{runId:guid}", async (HttpContext C, Guid id, Guid runId, string profileId, [FromServices] AutomationService Service, CancellationToken Token) =>
            TypedResults.Ok(await Service.RunDetailAsync(Access(C, profileId), id, runId, Token)));
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
