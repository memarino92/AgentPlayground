using System.Net.Http.Json;

using PersonalAgent.Contracts.Configuration;

namespace PersonalAgent.Web.Services;

internal partial class PersonalAgentClient
{
    private const string WhatboxSettingsPath = "/api/admin/settings/whatbox";

    public Task<WhatboxSettingsResponse> GetWhatboxSettingsAsync(CancellationToken Token) =>
        SendWhatboxAsync<WhatboxSettingsResponse>(HttpMethod.Get, WhatboxSettingsPath, null, Token);

    public Task<WhatboxSettingsResponse> SaveWhatboxSettingsAsync(SaveWhatboxSettingsRequest Request, CancellationToken Token) =>
        SendWhatboxAsync<WhatboxSettingsResponse>(HttpMethod.Put, WhatboxSettingsPath, JsonContent.Create(Request, options: JsonOptions), Token);

    public Task<WhatboxConnectionResult> TestWhatboxConnectionAsync(CancellationToken Token) =>
        SendWhatboxAsync<WhatboxConnectionResult>(HttpMethod.Post, WhatboxSettingsPath + "/test", null, Token);

    private async Task<T> SendWhatboxAsync<T>(HttpMethod Method, string Path, HttpContent? Content, CancellationToken Token) where T : class
    {
        using var response = await SendAsync(Method, Path, Content, cancellationToken: Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Token)
            ?? throw new InvalidOperationException("No Whatbox settings response was returned.");
    }
}
