using System.Net;
using PersonalAgent.Contracts.Automations;

namespace PersonalAgent.Web.Services;

internal partial class PersonalAgentClient
{
    public async Task<Guid> UploadSandboxInputAsync(string Profile, byte[] Bytes)
    {
        using var Content = new ByteArrayContent(Bytes);
        return await UploadSandboxInputAsync(Profile, Content, null, CancellationToken.None);
    }

    internal async Task<Guid> UploadSandboxInputAsync(string Profile, HttpContent Content, System.Security.Claims.ClaimsPrincipal? User, CancellationToken Token)
    {
        using var Response = await SendAsync(HttpMethod.Post, $"/api/automations/inputs?profileId={Uri.EscapeDataString(Profile)}", Content, Token, requestUser: User);
        Response.EnsureSuccessStatusCode();
        return await Response.Content.ReadFromJsonAsync<Guid>(Token);
    }

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
    public Task<HttpResponseMessage> DownloadSandboxArtifactAsync(Guid Id, Guid Run, int Step, int? File, string Profile,
        System.Security.Claims.ClaimsPrincipal User, CancellationToken Token, bool Preview = false) =>
        SendAsync(HttpMethod.Get, $"/api/automations/{Id}/runs/{Run}/artifacts/{Step}?profileId={Uri.EscapeDataString(Profile)}"
            + (File is null ? "" : $"&file={File}") + (Preview ? "&preview=true" : ""), cancellationToken: Token, requestUser: User, streamResponse: true);

    public async Task ControlAutomationAsync(string Profile, Guid Id, string Operation, CancellationToken Token)
    {
        using var Response = await SendAsync(HttpMethod.Post, $"/api/automations/{Id}/{Operation}?profileId={Uri.EscapeDataString(Profile)}", cancellationToken: Token);
        Response.EnsureSuccessStatusCode();
    }
}
