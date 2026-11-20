using PersonalAgent.Contracts.Coding;

namespace PersonalAgent.Web.Services;

internal partial class PersonalAgentClient
{
    public async Task<CodingQuestion[]> GetCodingQuestionsAsync(string Profile, Guid? Session, CancellationToken Token)
    {
        var Url = $"/api/coding-jobs/questions?profileId={Uri.EscapeDataString(Profile)}";
        if (Session is not null) Url += $"&sessionId={Session:D}";
        using var Response = await SendAsync(HttpMethod.Get, Url, cancellationToken: Token);
        Response.EnsureSuccessStatusCode();
        return (await Response.Content.ReadFromJsonAsync<CodingQuestion[]>(Token))!;
    }

    public async Task<bool> HasCodingMessagesAsync(string Profile, Guid Session, long After, CancellationToken Token)
    {
        using var Response = await SendAsync(HttpMethod.Get,
            $"/api/coding-jobs/conversation-updates?profileId={Uri.EscapeDataString(Profile)}&sessionId={Session:D}&after={After}",
            cancellationToken: Token);
        Response.EnsureSuccessStatusCode();
        return await Response.Content.ReadFromJsonAsync<bool>(Token);
    }

    public async Task AnswerCodingQuestionAsync(string Profile, Guid Id, string Answer, CancellationToken Token)
    {
        using var Response = await SendAsync(HttpMethod.Post, $"/api/coding-jobs/questions/{Id:D}/answer?profileId={Uri.EscapeDataString(Profile)}",
            JsonContent.Create(new AnswerCodingQuestion(Answer)), cancellationToken: Token);
        Response.EnsureSuccessStatusCode();
    }

    public async Task<CodingModelOption[]> GetCodingModelsAsync(CancellationToken Token)
    {
        using var Response = await SendAsync(HttpMethod.Get, "/api/admin/coding/models", cancellationToken: Token);
        Response.EnsureSuccessStatusCode(); return (await Response.Content.ReadFromJsonAsync<CodingModelOption[]>(Token))!;
    }
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
