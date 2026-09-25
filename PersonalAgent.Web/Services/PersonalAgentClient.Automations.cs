using System.Net;
using PersonalAgent.Contracts.Automations;

namespace PersonalAgent.Web.Services;

internal partial class PersonalAgentClient
{
    public async Task<IReadOnlyList<AutomationSummary>> GetAutomationsAsync(string Profile, int Offset, CancellationToken Token)
    {
        using var Response = await SendAsync(HttpMethod.Get, $"/api/automations/?profileId={Uri.EscapeDataString(Profile)}&offset={Offset}", cancellationToken: Token);
        Response.EnsureSuccessStatusCode();
        return await Response.Content.ReadFromJsonAsync<List<AutomationSummary>>(Token) ?? [];
    }
    public async Task<AutomationDetail?> GetAutomationAsync(string Profile, Guid Id, int RunOffset, CancellationToken Token)
    {
        using var Response = await SendAsync(HttpMethod.Get, $"/api/automations/{Id}?profileId={Uri.EscapeDataString(Profile)}&runOffset={RunOffset}", cancellationToken: Token);
        if (Response.StatusCode == HttpStatusCode.NotFound) return null;
        Response.EnsureSuccessStatusCode();
        return await Response.Content.ReadFromJsonAsync<AutomationDetail>(Token);
    }
    public async Task<AutomationRunDetail?> GetAutomationRunAsync(string Profile, Guid Id, Guid Run, CancellationToken Token)
    {
        using var Response = await SendAsync(HttpMethod.Get, $"/api/automations/{Id}/runs/{Run}?profileId={Uri.EscapeDataString(Profile)}", cancellationToken: Token);
        if (Response.StatusCode == HttpStatusCode.NotFound) return null;
        Response.EnsureSuccessStatusCode();
        return await Response.Content.ReadFromJsonAsync<AutomationRunDetail>(Token);
    }
    public async Task ControlAutomationAsync(string Profile, Guid Id, string Operation, CancellationToken Token)
    {
        using var Response = await SendAsync(HttpMethod.Post, $"/api/automations/{Id}/{Operation}?profileId={Uri.EscapeDataString(Profile)}", cancellationToken: Token);
        Response.EnsureSuccessStatusCode();
    }
}
