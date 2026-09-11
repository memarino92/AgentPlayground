using AgentPlayground.Integrations;
using Microsoft.Extensions.Options;
using PersonalAgent.Configuration;
using PersonalAgent.Security;
using PersonalAgent.Services;

namespace PersonalAgent.Endpoints;

internal static class IntegrationSettingsEndpoints
{
    public static void MapIntegrationSettings(this RouteGroupBuilder Api, IOptions<SecurityOptions> Security, IConfiguration Configuration)
    {
        var settings = Api.MapGroup("/admin/settings")
            .AddEndpointFilter(new SignedActorFilter(Security, ownerOnly: true))
            .AddEndpointFilter(new IntegrationAdministratorFilter(Configuration));
        settings.MapGet("", ([Microsoft.AspNetCore.Mvc.FromServices] DatabaseSettingsStore Store, CancellationToken CancellationToken) =>
            ExecuteAsync(async () => TypedResults.Ok(await Store.ReadAsync(CancellationToken))))
            .WithName("GetDatabaseSettings").WithSummary("Read all startup settings with secrets omitted");
        settings.MapPut("", (SaveDatabaseSettingsRequest Request, [Microsoft.AspNetCore.Mvc.FromServices] DatabaseSettingsStore Store, CancellationToken CancellationToken) =>
            ExecuteAsync(async () =>
            {
                await Store.SaveAsync(Request, CancellationToken);
                return TypedResults.NoContent();
            }))
            .WithName("SaveDatabaseSettings").WithSummary("Save existing settings atomically; affected services require restart");

        var group = Api.MapGroup("/admin/integrations/sentry")
            .AddEndpointFilter(new SignedActorFilter(Security, ownerOnly: true))
            .AddEndpointFilter(new IntegrationAdministratorFilter(Configuration));

        group.MapGet("", ([Microsoft.AspNetCore.Mvc.FromServices] IIntegrationSettingsService Service, CancellationToken CancellationToken) =>
            ExecuteAsync(async () => TypedResults.Ok(await Service.GetAsync(CancellationToken))))
            .WithName("GetSentrySettings").WithSummary("Read Sentry settings without secret values");
        group.MapPut("", (SaveIntegrationRequest Request, HttpContext Context, [Microsoft.AspNetCore.Mvc.FromServices] IIntegrationSettingsService Service, CancellationToken CancellationToken) =>
            ExecuteAsync(async () => TypedResults.Ok(await Service.SaveAsync(Request, SignedActorFilter.Get(Context).ActorId, CancellationToken))))
            .WithName("SaveSentrySettings").WithSummary("Validate and save a candidate Sentry configuration");
        group.MapPost("/apply", (ApplyIntegrationRequest Request, HttpContext Context, [Microsoft.AspNetCore.Mvc.FromServices] IIntegrationSettingsService Service, CancellationToken CancellationToken) =>
            ExecuteAsync(async () => TypedResults.Ok(await Service.ApplyAsync(Request.Revision, SignedActorFilter.Get(Context).ActorId, CancellationToken))))
            .WithName("ApplySentrySettings").WithSummary("Promote the saved revision and reload API; other services reconcile within 15 seconds");
        group.MapPost("/reload", ([Microsoft.AspNetCore.Mvc.FromServices] IIntegrationSettingsService Service, CancellationToken CancellationToken) =>
            ExecuteAsync(async () => TypedResults.Ok(await Service.ReloadAsync(CancellationToken))))
            .WithName("ReloadSentrySettings").WithSummary("Reload active integration settings in API and read service acknowledgements");
        group.MapPost("/test", (ApplyIntegrationRequest Request, [Microsoft.AspNetCore.Mvc.FromServices] IIntegrationSettingsService Service, CancellationToken CancellationToken) =>
            ExecuteAsync(async () => TypedResults.Ok(await Service.TestAsync(Request.Revision, CancellationToken))))
            .WithName("TestSentrySettings").WithSummary("Queue a synthetic event using the requested active API revision");
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> Operation)
    {
        try { return await Operation(); }
        catch (IntegrationValidationException Exception) { return Results.ValidationProblem(Exception.Errors); }
        catch (IntegrationConflictException) { return Results.Problem(statusCode: 409, title: "Settings changed. Refresh before saving or applying."); }
        catch (Exception Exception) when (Exception is Npgsql.NpgsqlException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        { return Results.Problem(statusCode: 503, title: "Integration settings are unavailable. Check database bootstrap and service status."); }
    }
}
