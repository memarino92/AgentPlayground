using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PersonalAgent.Api.Automations;
using PersonalAgent.Api.Configuration;
using PersonalAgent.Api.Security;
using PersonalAgent.Contracts.Automations;
using PersonalAgent.Integrations;

namespace PersonalAgent.Api.Endpoints;

internal static class AutomationRuntimeEndpoints
{
    public static void MapAutomationRuntimeSettings(this RouteGroupBuilder Api, IOptions<SecurityOptions> Security, IConfiguration Configuration)
    {
        var Group = Api.MapGroup("/admin/automation-runtime").AddEndpointFilter(new SignedActorFilter(Security, ownerOnly: true))
            .AddEndpointFilter(new IntegrationAdministratorFilter(Configuration));
        Group.MapGet("", async ([FromServices] AutomationRuntimeStore Store, CancellationToken Token) => Results.Ok((await Store.ReadAsync(Token)).View));
        Group.MapPut("", async (SaveAutomationRuntime Request, [FromServices] AutomationRuntimeStore Store, CancellationToken Token) =>
        {
            try { return Results.Ok(await Store.SaveAsync(Request, Token)); }
            catch (ArgumentException E) { return Results.BadRequest(new { error = E.Message }); }
            catch (IntegrationConflictException) { return Results.Conflict(new { error = "Settings changed. Reload before saving." }); }
        });
    }

    public static void MapAutomationRuntimeGateway(this WebApplication App)
    {
        App.MapPost("/automation-runtime/{runId:guid}/{step:int}/tools", async (Guid runId, int step, HttpContext Context,
            [FromServices] AutomationOperationGateway Gateway, [FromServices] ILogger<AutomationOperationGateway> Logger, CancellationToken Token) =>
        {
            Context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = 16384;
            var Header = Context.Request.Headers.Authorization.ToString();
            if (!Header.StartsWith("Bearer ", StringComparison.Ordinal) || Header.Length != 71) return Results.Unauthorized();
            using var Deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            Deadline.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var Request = await Context.Request.ReadFromJsonAsync<AutomationOperationRequest>(Deadline.Token);
                if (Request is null) return Results.BadRequest();
                return Results.Ok(new { output = await Gateway.InvokeAsync(runId, step, Header[7..], Request, Deadline.Token) });
            }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid operation or reused operation ID." }); }
            catch (System.Text.Json.JsonException) { return Results.BadRequest(); }
            catch (OperationCanceledException) { return Results.StatusCode(504); }
            catch (Exception E)
            {
                Logger.LogError(new EventId(4307), "Automation gateway {RunId}/{StepIndex} failed with {ExceptionType}", runId, step, E.GetType().Name);
                return Results.Problem(statusCode: 502, title: "Automation tool operation failed.");
            }
        }).RequireRateLimiting(PersonalAgentConstants.ApiRateLimiter);
    }
}
