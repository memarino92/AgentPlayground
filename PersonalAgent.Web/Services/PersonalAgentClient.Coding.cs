using PersonalAgent.Contracts.Coding;

namespace PersonalAgent.Web.Services;

internal partial class PersonalAgentClient
{
    public async Task<CodingSettingsView> GetCodingSettingsAsync(CancellationToken Token)
    {
        using var Response = await SendAsync(HttpMethod.Get, "/api/admin/coding", cancellationToken: Token);
        Response.EnsureSuccessStatusCode(); return (await Response.Content.ReadFromJsonAsync<CodingSettingsView>(Token))!;
    }
    public async Task<CodingSettingsView> SaveCodingSettingsAsync(SaveCodingSettings Request, CancellationToken Token)
    {
        using var Response = await SendAsync(HttpMethod.Put, "/api/admin/coding", JsonContent.Create(Request), cancellationToken: Token);
        Response.EnsureSuccessStatusCode(); return (await Response.Content.ReadFromJsonAsync<CodingSettingsView>(Token))!;
    }
    public async Task<CodingJobView[]> GetCodingJobsAsync(string Profile, CancellationToken Token)
    {
        using var Response = await SendAsync(HttpMethod.Get, $"/api/coding-jobs?profileId={Uri.EscapeDataString(Profile)}", cancellationToken: Token);
        Response.EnsureSuccessStatusCode(); return (await Response.Content.ReadFromJsonAsync<CodingJobView[]>(Token))!;
    }
    public async Task CancelCodingJobAsync(string Profile, Guid Id, CancellationToken Token)
    {
        using var Response = await SendAsync(HttpMethod.Post, $"/api/coding-jobs/{Id}/cancel?profileId={Uri.EscapeDataString(Profile)}", cancellationToken: Token);
        Response.EnsureSuccessStatusCode();
    }
    public async Task<CodingJobView> GetCodingJobAsync(string Profile, Guid Id, CancellationToken Token)
    {
        using var Response = await SendAsync(HttpMethod.Get, $"/api/coding-jobs/{Id}?profileId={Uri.EscapeDataString(Profile)}", cancellationToken: Token);
        Response.EnsureSuccessStatusCode(); return (await Response.Content.ReadFromJsonAsync<CodingJobView>(Token))!;
    }
}
