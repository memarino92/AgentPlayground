namespace PersonalAgent.Api.Security;

internal sealed class IntegrationAdministratorFilter(IConfiguration Configuration) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext Context, EndpointFilterDelegate Next)
    {
        var actor = SignedActorFilter.Get(Context.HttpContext);
        if (!IsAllowed(Configuration, actor.ActorId, actor.Role))
            return ValueTask.FromResult<object?>(Results.Problem(statusCode: 403, title: "Deployment administrator access is required."));
        return Next(Context);
    }

    internal static bool IsAllowed(IConfiguration Configuration, string ActorId, string Role)
    {
        var allowed = (Environment.GetEnvironmentVariable("INTEGRATION_SETTINGS_ADMINISTRATORS")
            ?? Configuration["IntegrationSettings:AdministratorIds"] ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return Role == "Owner" && allowed.Contains(ActorId, StringComparer.OrdinalIgnoreCase);
    }
}
