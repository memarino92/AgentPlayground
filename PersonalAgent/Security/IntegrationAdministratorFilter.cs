namespace PersonalAgent.Security;

internal sealed class IntegrationAdministratorFilter(IConfiguration Configuration) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext Context, EndpointFilterDelegate Next)
    {
        var actor = SignedActorFilter.Get(Context.HttpContext);
        var allowed = (Environment.GetEnvironmentVariable("INTEGRATION_SETTINGS_ADMINISTRATORS")
            ?? Configuration["IntegrationSettings:AdministratorIds"] ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (actor.Role != "Owner" || !allowed.Contains(actor.ActorId, StringComparer.OrdinalIgnoreCase))
            return ValueTask.FromResult<object?>(Results.Problem(statusCode: 403, title: "Deployment administrator access is required."));
        return Next(Context);
    }
}
