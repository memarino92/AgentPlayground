using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using PersonalAgent.Api.Coding;
using PersonalAgent.Api.Configuration;
using PersonalAgent.Api.Models;
using PersonalAgent.Api.Security;
using PersonalAgent.Contracts.Coding;
using PersonalAgent.Integrations;

namespace PersonalAgent.Api.Endpoints;

internal static class CodingJobEndpoints
{
    public static void MapCodingJobs(this RouteGroupBuilder Api, IOptions<SecurityOptions> Security, IConfiguration Configuration)
    {
        var Settings = Api.MapGroup("/admin/coding").AddEndpointFilter(new SignedActorFilter(Security, ownerOnly: true))
            .AddEndpointFilter(new IntegrationAdministratorFilter(Configuration));
        Settings.MapGet("", async ([FromServices] CodingJobStore Store, CancellationToken Token) => Results.Ok((await Store.SettingsAsync(Token)).View));
        Settings.MapPut("", async (SaveCodingSettings Request, [FromServices] CodingJobStore Store, CancellationToken Token) =>
        {
            try { return Results.Ok(await Store.SaveSettingsAsync(Request, Token)); }
            catch (ArgumentException E) { return Results.BadRequest(new { error = E.Message }); }
            catch (IntegrationConflictException) { return Results.Conflict(); }
        });
        var Group = Api.MapGroup("/coding-jobs").AddEndpointFilter(new SignedActorFilter(Security, ownerOnly: true));
        Group.AddEndpointFilter(async (Context, Next) =>
        {
            try { return await Next(Context); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException E) { return Results.BadRequest(new { error = E.Message }); }
            catch (InvalidOperationException E) { return Results.Conflict(new { error = E.Message }); }
            catch (HttpRequestException) { return Results.Problem(statusCode: 502, title: "Repository access or branch protection check failed."); }
        });
        Group.MapPost("", async (HttpContext C, string profileId, StartCodingJob Request, [FromServices] CodingJobService Service, CancellationToken Token) =>
            Results.Accepted(value: await Service.StartAsync(Access(C, profileId), Request, Token)));
        Group.MapGet("", async (HttpContext C, string profileId, [FromServices] CodingJobService Service, CancellationToken Token) =>
            Results.Ok(await Service.ListAsync(Access(C, profileId), Token)));
        Group.MapGet("/{id:guid}", async (HttpContext C, string profileId, Guid id, [FromServices] CodingJobService Service, CancellationToken Token) =>
            Results.Ok(await Service.InspectAsync(Access(C, profileId), id, false, Token)));
        Group.MapPost("/{id:guid}/cancel", async (HttpContext C, string profileId, Guid id, [FromServices] CodingJobService Service, CancellationToken Token) =>
            Results.Ok(await Service.InspectAsync(Access(C, profileId), id, true, Token)));
    }

    public static void MapCodingModelGateway(this WebApplication App) => App.MapPost("/coding-runtime/{id:guid}/v1/chat/completions",
        async (Guid id, HttpContext Context, [FromServices] CodingModelGateway Gateway, CancellationToken Token) =>
        {
            var Limit = Context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (Limit is not null) Limit.MaxRequestBodySize = 1_000_000;
            try { await Gateway.RelayAsync(id, Context, Token); }
            catch (Exception E) when (E is UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
                if (Context.Response.HasStarted) Context.Abort();
                else Context.Response.StatusCode = E is UnauthorizedAccessException ? 403 : E is ArgumentException or System.Text.Json.JsonException ? 400 : 502;
            }
        }).RequireRateLimiting(PersonalAgentConstants.ApiRateLimiter);

    private static AgentAccessContext Access(HttpContext C, string Profile)
    {
        var A = SignedActorFilter.Get(C);
        return new(A.ActorId, A.Role, Profile) { Email = A.Email };
    }
}
