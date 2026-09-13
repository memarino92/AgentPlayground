using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.JSInterop;
using PersonalAgent.Web.Services;

namespace PersonalAgent.Web.Components.Pages;

public partial class Chat
{
    private const string MessageListContainerId = "chat-message-list";
    private const int SessionPageSize = 20;
    private string? sessionId;
    private string? profileId;
    private string? actorId;
    private string role = "Owner";
    private string? selectedModelId;
    private string? activeModelId;
    private List<SessionListItem> sessions = new();
    private List<ConversationMessage> messages = new();
    private List<AvailableChatModelResponse> availableModels = new();
    private string currentMessage = string.Empty;
    private bool isChangingModel;
    private bool isLoadingHistory;
    private bool isSendingMessage;
    private bool isCreatingSession;
    private bool isLoadingSessions;
    private string? errorMessage;
    private bool hasAttemptedRestore;
    private bool isSessionDrawerOpen;
    private DateTimeOffset? nextBeforeActivityAt;
    private Guid? nextBeforeSessionId;
    private bool hasMoreSessions;
    private bool shouldScrollToBottom;
    private bool isReadOnly;

    private bool isBusy => isLoadingHistory || isSendingMessage || isCreatingSession || isChangingModel;
    private string? activeModelDisplayName => availableModels.FirstOrDefault(model => model.Id == activeModelId)?.DisplayName ?? activeModelId;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !hasAttemptedRestore)
        {
            hasAttemptedRestore = true;
            NavigationManager.LocationChanged += HandleLocationChanged;
            await InitializeAsync();
            StateHasChanged();
            return;
        }

        if (!shouldScrollToBottom) return;
        shouldScrollToBottom = false;
        await JsRuntime.InvokeVoidAsync("scrollToChatBottom", MessageListContainerId);
    }

    private async Task InitializeAsync()
    {
        try
        {
            await HandleQueryActionsAsync();
            var resolvedProfileId = await ResolveAccessAsync();
            if (string.IsNullOrWhiteSpace(resolvedProfileId))
            {
                return;
            }

            profileId = resolvedProfileId;
            await LoadModelsAsync();
            await LoadSessionsAsync();
            var query = QueryHelpers.ParseQuery(NavigationManager.ToAbsoluteUri(NavigationManager.Uri).Query);
            if (query.TryGetValue("sessionId", out var requested) && Guid.TryParse(requested, out var requestedId))
                await SelectSessionAsync(requestedId.ToString());

        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }
    }

    private Task HandleQueryActionsAsync()
    {
        var uri = NavigationManager.ToAbsoluteUri(NavigationManager.Uri);
        var query = QueryHelpers.ParseQuery(uri.Query);
        isSessionDrawerOpen = query.TryGetValue("drawer", out var drawerValue) && drawerValue == "1";
        return Task.CompletedTask;
    }

    private async Task CreateSavedSessionAsync()
    {
        isCreatingSession = true;
        errorMessage = null;
        try
        {
            var resolvedProfileId = await ResolveAccessAsync();
            if (string.IsNullOrWhiteSpace(resolvedProfileId))
            {
                errorMessage = role == "Coach"
                    ? "No athlete profile is assigned to this coach account."
                    : "Unable to determine your profile id for chat memory.";
                return;
            }

            profileId = resolvedProfileId;

            var response = await ApiClient.CreateSessionAsync(resolvedProfileId, selectedModelId, actorId, role);
            if (response != null)
            {
                activeModelId = response.ModelId;
                selectedModelId = response.ModelId;
                await SelectSessionAsync(response.SessionId, []);
                await LoadSessionsAsync();
            }
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }
        finally
        {
            isCreatingSession = false;
        }
    }

    private async Task SendMessage()
    {
        if (isBusy || string.IsNullOrWhiteSpace(currentMessage) || profileId == null || selectedModelId is null) return;
        if (sessionId is null) await CreateSavedSessionAsync();
        if (sessionId is null) return;

        errorMessage = null;
        var sendingSessionId = sessionId;
        var sendingProfileId = profileId;
        var userMessage = currentMessage;
        currentMessage = string.Empty;
        messages.Add(new ConversationMessage("user", userMessage));
        shouldScrollToBottom = true;
        isSendingMessage = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            var response = await ApiClient.SendMessageAsync(sendingSessionId, sendingProfileId, userMessage, actorId, role);
            if (response != null && sessionId == sendingSessionId)
            {
                messages.Add(new ConversationMessage("assistant", response.Response));
                UpdateSessionSummary(sessionId, userMessage, DateTimeOffset.UtcNow);
                shouldScrollToBottom = true;
            }
        }
        catch (Exception ex)
        {
            if (sessionId == sendingSessionId)
            {
                if (messages.Count > 0) messages.RemoveAt(messages.Count - 1);
                currentMessage = userMessage;
                errorMessage = ex.Message;
            }
        }
        finally
        {
            isSendingMessage = false;
        }
    }

    private Task ResetSelectionAsync()
    {
        sessionId = null;
        activeModelId = null;
        messages.Clear();
        currentMessage = string.Empty;
        errorMessage = null;
        isReadOnly = false;
        SaveChatQuery();
        return Task.CompletedTask;
    }

    private async Task SelectSessionAsync(string selectedSessionId, List<ConversationMessage>? knownMessages = null)
    {
        if (profileId is null) return;

        sessionId = selectedSessionId;
        isReadOnly = false;
        errorMessage = null;
        SaveChatQuery();

        if (knownMessages is not null)
        {
            messages = [.. knownMessages];
            shouldScrollToBottom = true;
            return;
        }

        await LoadSelectedSessionHistoryAsync(selectedSessionId);
    }

    private async Task HandleSessionSelectionChanged(string? selectedSessionId)
    {
        if (string.IsNullOrWhiteSpace(selectedSessionId))
        {
            await ResetSelectionAsync();
            return;
        }

        if (selectedSessionId == sessionId) return;
        currentMessage = string.Empty;
        await SelectSessionAsync(selectedSessionId);
    }

    private async Task LoadSelectedSessionHistoryAsync(string selectedSessionId)
    {
        if (profileId is null) return;

        isLoadingHistory = true;
        try
        {
            var history = await ApiClient.GetHistoryAsync(selectedSessionId, profileId, actorId, role);
            if (history is null)
            {
                if (sessionId == selectedSessionId)
                    await ResetSelectionAsync();

                sessions.RemoveAll(session => session.SessionId == selectedSessionId);
                return;
            }

            if (sessionId != selectedSessionId) return;
            sessionId = history.SessionId;
            isReadOnly = history.IsReadOnly;
            activeModelId = history.ModelId;
            selectedModelId = history.ModelId;
            messages = [.. history.Messages];
            shouldScrollToBottom = true;
        }
        catch (PersonalAgentApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            if (sessionId == selectedSessionId)
                await ResetSelectionAsync();

            sessions.RemoveAll(session => session.SessionId == selectedSessionId);
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }
        finally
        {
            isLoadingHistory = false;
        }
    }

    private async Task HandleKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !e.ShiftKey && !isBusy && !string.IsNullOrWhiteSpace(currentMessage))
        {
            await SendMessage();
        }
    }

    private Task HandleCurrentMessageChanged(string value)
    {
        currentMessage = value;
        return Task.CompletedTask;
    }

    private string ModelStorageKey => $"personal-agent-model:{actorId}:{profileId}";

    private async Task HandleSelectedModelChanged(string value)
    {
        if (!availableModels.Any(model => model.Id == value) || isBusy) return;
        isChangingModel = true;
        try
        {
            if (sessionId is not null && profileId is not null)
                await ApiClient.SetChatModelAsync(sessionId, profileId, value);
            selectedModelId = value;
            if (sessionId is not null) activeModelId = value;
            await ModelStorage.SetAsync(ModelStorageKey, value);
        }
        catch (Exception ex) { errorMessage = ex.Message; }
        finally { isChangingModel = false; }
    }

    private async Task CreateSession()
    {
        if (isBusy) return;
        if (sessionId is not null && messages.Count == 0 && !isReadOnly)
        {
            currentMessage = string.Empty;
            errorMessage = null;
            return;
        }
        await ResetSelectionAsync();
        var stored = await ModelStorage.GetAsync<string>(ModelStorageKey);
        selectedModelId = stored.Success && availableModels.Any(model => model.Id == stored.Value) ? stored.Value
            : availableModels.Any(model => model.Id == selectedModelId) ? selectedModelId
            : availableModels.FirstOrDefault(model => model.IsDefault)?.Id ?? availableModels.FirstOrDefault()?.Id;
    }

    private void SaveChatQuery()
    {
        var uri = NavigationManager.GetUriWithQueryParameters(new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId, ["profileId"] = profileId, ["drawer"] = isSessionDrawerOpen ? "1" : null
        });
        if (uri != NavigationManager.Uri) NavigationManager.NavigateTo(uri);
    }

    private void CloseSessionDrawer() { isSessionDrawerOpen = false; SaveChatQuery(); }
    private void ToggleSessionDrawer() { isSessionDrawerOpen = !isSessionDrawerOpen; SaveChatQuery(); }

    private async void HandleLocationChanged(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e)
    {
        if (NavigationManager.ToAbsoluteUri(e.Location).AbsolutePath != "/chat") return;
        try
        {
            await InvokeAsync(async () =>
            {
                await HandleQueryActionsAsync();
                var query = QueryHelpers.ParseQuery(NavigationManager.ToAbsoluteUri(e.Location).Query);
                var requested = query.TryGetValue("sessionId", out var value) && Guid.TryParse(value, out var id) ? id.ToString() : null;
                if (requested != sessionId)
                {
                    currentMessage = string.Empty;
                    profileId = await ResolveAccessAsync();
                    if (requested is null) await ResetSelectionAsync();
                    else await SelectSessionAsync(requested);
                }
                StateHasChanged();
            });
        }
        catch (Exception ex) { await DispatchExceptionAsync(ex); }
    }

    public void Dispose() => NavigationManager.LocationChanged -= HandleLocationChanged;

    private async Task LoadSessionsAsync(bool append = false)
    {
        if (profileId is null) return;

        isLoadingSessions = true;
        errorMessage = null;

        try
        {
            var page = await ApiClient.GetSessionsAsync(profileId, append ? nextBeforeActivityAt : null, append ? nextBeforeSessionId : null, SessionPageSize, actorId, role);
            if (page is null) return;

            if (!append)
                sessions.Clear();

            sessions = append
                ? [.. sessions, .. page.Sessions.Where(candidate => sessions.All(existing => existing.SessionId != candidate.SessionId))]
                : [.. page.Sessions];

            hasMoreSessions = page.HasMore;
            nextBeforeActivityAt = page.NextBeforeActivityAt;
            nextBeforeSessionId = page.NextBeforeSessionId;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }
        finally
        {
            isLoadingSessions = false;
        }
    }

    private async Task LoadModelsAsync()
    {
        var response = await ApiClient.GetModelsAsync();
        availableModels = response?.Models ?? [];
        var stored = await ModelStorage.GetAsync<string>(ModelStorageKey);
        if (stored.Success && availableModels.Any(model => model.Id == stored.Value)) selectedModelId = stored.Value;
        selectedModelId ??= availableModels.FirstOrDefault(model => model.IsDefault)?.Id ?? availableModels.FirstOrDefault()?.Id;
    }

    private async Task LoadMoreSessionsAsync()
    {
        if (!hasMoreSessions) return;
        await LoadSessionsAsync(append: true);
    }

    private void UpdateSessionSummary(string selectedSessionId, string snippet, DateTimeOffset lastActivityAt)
    {
        var trimmedSnippet = CreateSnippet(snippet);
        var existingIndex = sessions.FindIndex(session => session.SessionId == selectedSessionId);
        var createdAt = existingIndex >= 0 ? sessions[existingIndex].CreatedAt : lastActivityAt;
        var updated = new SessionListItem(selectedSessionId, trimmedSnippet, lastActivityAt, createdAt);

        if (existingIndex >= 0)
            sessions.RemoveAt(existingIndex);

        sessions.Insert(0, updated);
    }

    private string GetSelectedSessionOption() => sessionId ?? string.Empty;

    private static string CreateSnippet(string message)
    {
        var normalized = message.Trim();
        if (normalized.Length <= 72) return normalized;
        return normalized[..69] + "...";
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) => timestamp.ToLocalTime().ToString("g");

    private async Task<string?> ResolveAccessAsync()
    {
        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;
        if (user.IsInRole("Owner"))
        {
            role = "Owner";
            profileId = user.FindFirst("urn:github:login")?.Value;
            actorId = profileId;
            return profileId;
        }

        if (!user.IsInRole("Coach")) return null;
        role = "Coach";
        var subject = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var email = user.FindFirst(ClaimTypes.Email)?.Value;
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email)) return null;
        actorId = $"google:{subject}";
        var assignments = await ApiClient.GetAssignedProfilesAsync(actorId, email);
        var query = QueryHelpers.ParseQuery(NavigationManager.ToAbsoluteUri(NavigationManager.Uri).Query);
        var requestedProfile = query.TryGetValue("profileId", out var value) ? value.ToString() : null;
        profileId = requestedProfile is not null && assignments.Contains(requestedProfile, StringComparer.OrdinalIgnoreCase)
            ? requestedProfile : assignments.FirstOrDefault();
        return profileId;
    }
}
