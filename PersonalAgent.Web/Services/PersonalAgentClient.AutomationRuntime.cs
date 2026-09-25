using PersonalAgent.Contracts.Automations;

namespace PersonalAgent.Web.Services;

internal partial class PersonalAgentClient
{
    public async Task<AutomationRuntimeView> GetAutomationRuntimeAsync(CancellationToken Token)
    {
        using var Response = await SendAsync(HttpMethod.Get, "/api/admin/automation-runtime", cancellationToken: Token);
        Response.EnsureSuccessStatusCode();
        return (await Response.Content.ReadFromJsonAsync<AutomationRuntimeView>(Token))!;
    }
    public async Task<AutomationRuntimeView> SaveAutomationRuntimeAsync(SaveAutomationRuntime Request, CancellationToken Token)
    {
        using var Response = await SendAsync(HttpMethod.Put, "/api/admin/automation-runtime", JsonContent.Create(Request), cancellationToken: Token);
        Response.EnsureSuccessStatusCode();
        return (await Response.Content.ReadFromJsonAsync<AutomationRuntimeView>(Token))!;
    }
}
