using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;

using System.Text.Json.Nodes;

namespace PersonalAgent.Mobile.Services;

public sealed class NativeChatClient(HttpClient Http, PersonalAgentApiClient Settings)
{
    private const string SessionKey = "ChatSession.v1";

    private Uri ServiceUri()
    {
        if (!Uri.TryCreate(Settings.GetWebAppUrl(), UriKind.Absolute, out var Address) || Address.Scheme != "https" ||
            !string.IsNullOrEmpty(Address.UserInfo))
            throw new InvalidOperationException("Set an HTTPS chat service URL in Settings.");
        return new Uri(Address.GetLeftPart(UriPartial.Authority));
    }

    public Uri AutomationRunUri(Guid Id, Guid Run) =>
        new(ServiceUri(), $"/automations?automationId={Id}&runId={Run}&profileId={Uri.EscapeDataString(Profile ?? "")}");

    public async Task SignInAsync()
    {
        var Origin = ServiceUri();
        var Proof = RandomNumberGenerator.GetBytes(32);
        var Challenge = Convert.ToHexString(SHA256.HashData(Proof));
        var Callback = await WebAuthenticator.Default.AuthenticateAsync(new(Origin, $"/mobile/connect?challenge={Challenge}"),
            new Uri("personalagentmobile://chat"));
        if (!Callback.Properties.TryGetValue("grant", out var Grant) || string.IsNullOrWhiteSpace(Grant))
            throw new InvalidOperationException("Sign-in did not complete.");
        await SecureStorage.Default.SetAsync(SessionKey, $"{Origin.AbsoluteUri}\n{Grant}.{Convert.ToHexString(Proof)}");
    }

    public void SignOut()
    {
        SecureStorage.Default.Remove(SessionKey);
        Profiles = [];
        Profile = null;
        IsOwner = false;
    }

    public bool IsOwner { get; private set; }
    public string? Profile { get; private set; }
    public string[] Profiles { get; private set; } = [];

    public void SelectProfile(string Value)
    {
        if (Profiles.Contains(Value)) Profile = Value;
    }

    public async Task<JsonNode?> WorkspaceAsync(string Operation, Guid Id = default, string? Value = null,
        JsonNode? Data = null, long Sequence = 0, int Offset = 0, Guid Run = default, DateTimeOffset? Before = null)
    {
        using var Request = await CreateAsync($"/mobile/workspace/{Operation}");
        if (Request is null) throw new InvalidOperationException("Sign in to continue.");
        Request.Content = JsonContent.Create(new { ProfileId = Profile, Id, Value, Data, Sequence, Offset, Run, Before });
        using var Response = await Http.SendAsync(Request);
        Response.EnsureSuccessStatusCode();
        return await Response.Content.ReadFromJsonAsync<JsonNode>();
    }

    private async Task<HttpRequestMessage?> CreateAsync(string Path)
    {
        var Origin = ServiceUri();
        var Session = await SecureStorage.Default.GetAsync(SessionKey);
        if (Session?.Split('\n') is not [var SavedOrigin, var Bearer] || SavedOrigin != Origin.AbsoluteUri) return null;
        var Request = new HttpRequestMessage(HttpMethod.Post, new Uri(Origin, Path));
        Request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Bearer);
        return Request;
    }

    public async Task TransferAsync(string Operation, Guid Id, Stream? Input, Stream? Output, string? Name = null)
    {
        var path = $"/mobile/recordings/{Operation}/{Id}?profileId={Uri.EscapeDataString(Profile ?? "")}&name={Uri.EscapeDataString(Name ?? "")}";
        using var request = await CreateAsync(path) ?? throw new InvalidOperationException("Sign in to continue.");
        if (Input is not null) request.Content = new StreamContent(Input);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (Output is not null) await response.Content.CopyToAsync(Output);
    }

    public async Task<NativeConversation> SendAsync(Guid Id, string Message, Func<string, Task> OnDelta, CancellationToken Token)
    {
        using var request = await CreateAsync($"/mobile/chat/{Id}/stream") ?? throw new InvalidOperationException("Sign in to continue.");
        request.Content = JsonContent.Create(new { ProfileId = Profile, Message });
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(Token);
        return await PersonalAgent.Presentation.ConversationStream.ReadAsync<NativeConversation>(stream, OnDelta, Token);
    }

    public async Task<NativeConversation?> ReadAsync()
    {
        using var Request = await CreateAsync("/mobile/chat/open");
        if (Request is null)
        {
            Profile = null;
            IsOwner = false;
            Profiles = [];
            return null;
        }
        JsonNode? Account;
        try { Account = await WorkspaceAsync("account"); }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized) { SignOut(); return null; }
        IsOwner = Account?["owner"]?.GetValue<bool>() == true;
        Profiles = Account?["profiles"]?.AsArray().Select(item => item!.ToString()).ToArray() ?? [];
        Profile = Profiles.Contains(Profile) ? Profile : Profiles.FirstOrDefault();
        if (Profile is null) throw new InvalidOperationException("This account has no assigned profiles.");
        Request.Content = JsonContent.Create(new { ProfileId = Profile });
        using var Response = await Http.SendAsync(Request);
        if (Response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            SignOut();
            return null;
        }
        Response.EnsureSuccessStatusCode();
        return await Response.Content.ReadFromJsonAsync<NativeConversation>() ?? throw new InvalidDataException("No conversation was returned.");
    }
}

public sealed record NativeConversation(Guid SessionId, List<NativeChatMessage> Messages);
public sealed record NativeChatMessage(string Role, string Content)
{
    public long Sequence { get; init; }
    public PersonalAgent.Contracts.ChatPresentation? Presentation { get; init; }
    public string Speaker => Role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "You" : "PersonalAgent";
}
