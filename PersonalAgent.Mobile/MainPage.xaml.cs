using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Dispatching;

using PersonalAgent.Contracts;
using PersonalAgent.Mobile.Models;
using PersonalAgent.Mobile.Services;
using PersonalAgent.Mobile.ViewModels;

namespace PersonalAgent.Mobile;

public partial class MainPage : ContentPage
{
    private const string WaitingForReply = "Waiting for a reply…";
    private readonly MainViewModel _viewModel;
    private readonly NotificationRoutingService _notificationRoutingService;
    private readonly NativeChatClient _chat;
    private ObservableCollection<NativeChatMessage> _messages = [];
    private bool _followTranscript = true;
    private Guid? _conversation;
    private bool _chatBusy;
    private readonly ChatComposer _composer = new();
    private Guid? _run;
    private bool _runStarted;
    private bool _steering;
    private bool _uploadingAttachment;
    private TaskCompletionSource<bool>? _chatIdle;
    private async void OnStopClicked(object? sender, EventArgs e)
    {
        if (_conversation is not { } session || _run is not { } run) return;
        try { await _chat.StopAsync(session, run); }
        catch (Exception) { ChatStatus.Text = "Could not confirm stop. Refresh to check the turn."; }
    }
    private readonly FirebaseCloudMessagingBridge _firebaseBridge;
    private bool _isVisible;
    private Page VisiblePage => Navigation.ModalStack.LastOrDefault() is NavigationPage modal
        ? modal.CurrentPage : Shell.Current?.CurrentPage ?? this;
    private bool _isReviewingApproval;
    private bool _hasAppeared;
    private IDispatcherTimer? _codingUpdates;
    private bool _checkingCodingUpdates;

    public MainPage(MainViewModel viewModel, NotificationRoutingService notificationRoutingService, FirebaseCloudMessagingBridge firebaseBridge, NativeChatClient chat)
    {
        _viewModel = viewModel;
        _notificationRoutingService = notificationRoutingService;
        _firebaseBridge = firebaseBridge;
        BindingContext = _viewModel;
        _notificationRoutingService.PendingApprovalReceived += OnPendingApprovalReceived;
        _notificationRoutingService.CodingQuestionsRequested += OnCodingQuestionsRequested;

        _chat = chat;
        InitializeComponent();
    }

    private async void OnMenuClicked(object? sender, EventArgs e)
    {
        var choices = new List<string> { "Check-in preview" };
        if (_chat.Profile is not null)
        {
            choices.AddRange(["Chat history", "Chat options", "Scheduled jobs", "Automations", "Coach recordings"]);
            if (_chat.Profiles.Length > 1) choices.Add("Change profile");
            if (_chat.IsOwner) choices.AddRange(["Outstanding questions", "Coding jobs", "Administration"]);
        }
        choices.AddRange(["Approvals", "Settings", "Refresh"]);
        if (_chat.Profile is not null) choices.Add("Sign out");
        var choice = await DisplayActionSheetAsync("PersonalAgent", "Close", null, choices.ToArray());
        switch (choice)
        {
            case "Approvals": await ReviewPendingApprovalsAsync(true); return;
            case "Settings": await OpenSettingsAsync(); return;
            case "Refresh": await LoadChatAsync(); return;
            case "Sign out": SignOut(); return;
        }
        if (choice == "Change profile")
        {
            var profile = await DisplayActionSheetAsync("Profile", "Cancel", null, _chat.Profiles);
            if (profile is null || !_chat.Profiles.Contains(profile)) return;
            _chat.SelectProfile(profile);
            Draft.Text = string.Empty;
            ShowConversation(null);
            await LoadChatAsync();
        }
        else if (choice is not null && choices.Contains(choice))
        {
            await Navigation.PushAsync(new WorkspacePage(_chat, choice, _conversation ?? Guid.Empty));
        }
    }

    private async Task OpenSettingsAsync()
    {
        var page = new ContentPage { Title = "Settings", SafeAreaEdges = SafeAreaEdges.All, BindingContext = _viewModel, Content = BuildContent() };
        var done = new ToolbarItem { Text = "Done" };
        done.Clicked += async (_, _) => await Navigation.PopModalAsync();
        page.ToolbarItems.Add(done);
        await Navigation.PushModalAsync(new NavigationPage(page));
    }

    private async void OnSignInClicked(object? sender, EventArgs e) => await LoadChatAsync(signIn: true);

    private async void OnSendClicked(object? sender, EventArgs e)
    {
        if (_conversation is not { } session || string.IsNullOrWhiteSpace(Draft.Text) || _uploadingAttachment || _steering) return;
        if (!_chatBusy)
        {
            await LoadChatAsync(message: Draft.Text.Trim());
            return;
        }
        if (!_runStarted || _run is not { } run || _chatIdle is not { } idle) return;

        var direction = Draft.Text.Trim();
        _steering = true;
        Draft.Text = string.Empty;
        UpdateComposer();
        try
        {
            await _chat.StopAsync(session, run);
            if (!await idle.Task) throw new InvalidOperationException("The interrupted turn could not be confirmed.");
        }
        catch (Exception)
        {
            if (_conversation == session) RestoreDirection(direction);
            ChatStatus.Text = "Could not confirm the interrupted turn. Refresh before sending again.";
            return;
        }
        finally
        {
            _steering = false;
            UpdateComposer();
        }
        if (_conversation == session) await LoadChatAsync(message: direction, steering: true);
    }

    private void RestoreDirection(string direction) =>
        Draft.Text = string.IsNullOrWhiteSpace(Draft.Text) ? direction : direction + "\n\n" + Draft.Text;

    private void OnDraftChanged(object? sender, TextChangedEventArgs e) => UpdateComposer();

    private void UpdateComposer()
    {
        if (SendMessage is null) return;
        SendMessage.Text = _run is null && !_steering ? "Send" : "Steer";
        SendMessage.IsEnabled = !_steering && !_uploadingAttachment && (!_chatBusy || _runStarted)
            && !string.IsNullOrWhiteSpace(Draft.Text);
        AddAttachment.IsEnabled = !_uploadingAttachment;
    }

    private async void OnAddAttachmentClicked(object? sender, EventArgs e)
    {
        if (!_chat.IsOwner || _uploadingAttachment || _conversation is not { } session) return;
        _uploadingAttachment = true;
        UpdateComposer();
        try
        {
            if (await DisplayActionSheetAsync("Add attachment", "Cancel", null, "Choose file") != "Choose file") return;
            var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Choose a file (up to 16 MB)" });
            if (file is null) return;
            await using var stream = await file.OpenReadAsync();
            var id = await _chat.UploadAttachmentAsync(stream);
            if (_conversation != session) return;
            var name = string.Concat(file.FileName.Where(character => !char.IsControl(character)));
            if (name.Length > 255) name = name[..255];
            Draft.Text = (Draft.Text + $"\nAttached file {name} (attachment:{id})").TrimStart();
        }
        catch (OperationCanceledException) { }
        catch (InvalidDataException error) { ChatStatus.Text = error.Message; }
        catch (Exception) { ChatStatus.Text = "Could not upload the attachment."; }
        finally
        {
            _uploadingAttachment = false;
            UpdateComposer();
        }
    }

    private void SignOut()
    {
        _chat.SignOut();
        Draft.Text = string.Empty;
        ShowConversation(null);
    }

    private void ShowConversation(NativeConversation? conversation, bool preservePosition = false)
    {
        ClearPendingTurn();
        var messages = conversation?.Messages ?? [];
        if (!preservePosition || _conversation != conversation?.SessionId)
        {
            _followTranscript = true;
            _messages = new(messages);
            Transcript.ItemsSource = _messages;
        }
        else
        {
            for (var index = 0; index < messages.Count; index++)
            {
                var message = messages[index];
                if (index == _messages.Count) _messages.Add(message);
                else if (_messages[index] != message) _messages[index] = message;
            }
            while (_messages.Count > messages.Count) _messages.RemoveAt(_messages.Count - 1);
        }

        _conversation = conversation?.SessionId;
        AddAttachment.IsVisible = conversation is not null && _chat.IsOwner;
        Composer.IsVisible = conversation is not null;
        SignIn.IsVisible = conversation is null;
        ChatStatus.Text = conversation is null ? "Sign in to open your conversation." :
            conversation.Messages.Count == 0 ? "Start your conversation." : string.Empty;
        RequestTranscriptFollow();
    }

    private void ClearPendingTurn()
    {
        PendingTurn.IsVisible = PendingActivityToggle.IsVisible = PendingActivity.IsVisible = false;
        PendingMessage.Text = PendingReply.Text = PendingActivity.Text = string.Empty;
    }

    private void OnPendingActivityClicked(object? sender, EventArgs e) =>
        PendingActivity.IsVisible = !PendingActivity.IsVisible;

    private async Task<NativeConversation> StreamReplyAsync(Guid session, string message, PendingChatRun? pendingRun)
    {
        ClearPendingTurn();
        var run = pendingRun?.Id ?? Guid.NewGuid();
        _run = run;
        _runStarted = false;
        UpdateComposer();
        _followTranscript = true;
        StopReply.IsVisible = true;
        PendingMessage.Text = message;
        PendingTurn.IsVisible = true;
        RequestTranscriptFollow();

        var reply = new StringBuilder();
        var renderedLength = 0;
        var calls = new Dictionary<Guid, ChatToolActivity>();
        var activityChanged = false;
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(66);
        timer.Tick += (_, _) =>
        {
            if (reply.Length != renderedLength || activityChanged) RequestTranscriptFollow();
            if ((reply.Length != renderedLength || activityChanged) && ChatStatus.Text == WaitingForReply)
                ChatStatus.Text = string.Empty;
            if (reply.Length != renderedLength)
            {
                PendingReply.Text = reply.ToString();
                renderedLength = reply.Length;
            }
            if (activityChanged)
            {
                if (!PendingActivityToggle.IsVisible) PendingActivity.IsVisible = true;
                PendingActivityToggle.Text = $"View activity · {calls.Count}";
                PendingActivityToggle.IsVisible = true;
                PendingActivity.Text = string.Join("\n", calls.Values.Select(call => $"{call.Label} · {call.Status}"));
                activityChanged = false;
            }
        };
        timer.Start();
        try
        {
            return await _chat.SendAsync(session, message, delta =>
            {
                reply.Append(delta);
                return Task.CompletedTask;
            }, CancellationToken.None, activity =>
            {
                calls[activity.CallId] = activity;
                activityChanged = true;
                return Task.CompletedTask;
            }, run, pendingRun is not null, () =>
            {
                _runStarted = true;
                UpdateComposer();
                return Task.CompletedTask;
            });
        }
        finally
        {
            timer.Stop();
            // Keep an uncertain turn visible until an authoritative refresh replaces it,
            // including when a newer draft prevents restoring the submitted text.
            PendingReply.Text = reply.ToString();
            RequestTranscriptFollow();
        }
    }

    private async Task LoadChatAsync(bool signIn = false, string? message = null, bool steering = false)
    {
        if (_chatBusy) return;
        _chatBusy = true;
        var idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _chatIdle = idle;
        var completed = false;
        SignIn.IsEnabled = SendMessage.IsEnabled = WorkspaceAction.IsEnabled = false;
        ChatStatus.Text = message is null ? "Opening conversation…" : WaitingForReply;
        try
        {
            if (signIn)
            {
                await _chat.SignInAsync();
                Draft.Text = string.Empty;
            }
            var conversation = message is null ? await _chat.ReadAsync() : null;
            var pendingRun = conversation?.PendingRun;
            if (pendingRun is not null)
            {
                ShowConversation(conversation);
                message = pendingRun.Message;
                ChatStatus.Text = WaitingForReply;
            }
            if (message is not null && _conversation is Guid id)
            {
                if (pendingRun is not null || steering) conversation = await StreamReplyAsync(id, message, pendingRun);
                else await _composer.SendAsync(() => Draft.Text, Text => Draft.Text = Text,
                    async Text => { conversation = await StreamReplyAsync(id, Text, null); });
            }

            ShowConversation(conversation, preservePosition: message is not null);
            completed = true;
        }
        catch (TaskCanceledException) { ChatStatus.Text = "Request interrupted. Refresh to check the conversation."; }
        catch (OperationCanceledException) { ChatStatus.Text = "Sign-in canceled."; }
        catch (Exception ex)
        {
            ChatStatus.Text = message is null ? $"Could not open chat. {ex.Message}" : "Delivery uncertain. Refresh before sending again.";
        }
        finally
        {
            _run = null;
            _runStarted = false;
            StopReply.IsVisible = false;
            _chatBusy = false;
            SignIn.IsEnabled = WorkspaceAction.IsEnabled = true;
            if (!completed && steering && message is not null) RestoreDirection(message);
            UpdateComposer();
            idle.TrySetResult(completed);
        }
        await OpenRequestedCodingQuestionsAsync();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _isVisible = true;
        if (_codingUpdates is null)
        {
            _codingUpdates = Dispatcher.CreateTimer();
            _codingUpdates.Interval = TimeSpan.FromSeconds(10);
            _codingUpdates.Tick += OnCodingUpdateTick;
        }
        _codingUpdates.Start();
        var firstAppearance = !_hasAppeared;
        _hasAppeared = true;
        if (firstAppearance || _conversation.HasValue) await LoadChatAsync();
        var hasCurrentToken = await _firebaseBridge.InitializeAsync();
        await _viewModel.InitializeAsync(hasCurrentToken);
        await ReviewPendingApprovalsAsync();
    }

    protected override void OnDisappearing()
    {
        _codingUpdates?.Stop();
        base.OnDisappearing();
    }

    private async void OnCodingUpdateTick(object? Sender, EventArgs Args)
    {
        if (!_isVisible || _chatBusy || _checkingCodingUpdates || !_chat.IsOwner || _conversation is not { } Session) return;
        var Profile = _chat.Profile;
        var After = _messages.LastOrDefault()?.Sequence ?? 0;
        _checkingCodingUpdates = true;
        try
        {
            if ((await _chat.WorkspaceAsync("coding-updates", Session, Sequence: After))?.GetValue<bool>() != true) return;
            var History = (await _chat.WorkspaceAsync("session", Session))?.Deserialize<NativeConversation>(JsonSerializerOptions.Web);
            if (_isVisible && !_chatBusy && _chat.Profile == Profile && _conversation == Session
                && (_messages.LastOrDefault()?.Sequence ?? 0) == After && History?.SessionId == Session)
                ShowConversation(History, preservePosition: true);
        }
        catch (Exception Error) when (Error is HttpRequestException or JsonException or InvalidOperationException) { }
        finally { _checkingCodingUpdates = false; }
    }

    public async Task SetActiveAsync(bool Active)
    {
        _isVisible = Active;
        if (Active && _hasAppeared)
        {
            await OpenRequestedCodingQuestionsAsync();
            await ReviewPendingApprovalsAsync();
        }
    }


    private async void OnRegisterDeviceClicked(object? sender, EventArgs e)
    {
        if (!await _firebaseBridge.InitializeAsync())
        {
            await DisplayAlertAsync("Register Device", "Firebase could not provide a current push token. Check your connection and notification permission, then try again.", "OK");
            return;
        }
        await _viewModel.RegisterDeviceAsync();
        await DisplayAlertAsync("Register Device", $"{_viewModel.StatusMessage}\nAPI: {_viewModel.GetApiBaseUrl()}", "OK");
    }

    private async void OnSaveProfileClicked(object? sender, EventArgs e)
    {
        await _viewModel.SaveProfileIdAsync();
        Draft.Text = string.Empty;
        ShowConversation(null);
        await LoadChatAsync();
        await DisplayAlertAsync("Profile", _viewModel.StatusMessage, "OK");
        await ReviewPendingApprovalsAsync();
    }

    private async void OnSaveConnectionClicked(object? sender, EventArgs e)
    {
        await _viewModel.SaveConnectionSettingsAsync();
        Draft.Text = string.Empty;
        ShowConversation(null);
        await LoadChatAsync();
        await DisplayAlertAsync("Connection", _viewModel.StatusMessage, "OK");
    }

    private async void OnPendingApprovalReceived(object? sender, PendingApprovalNotification notification)
    {
        await MainThread.InvokeOnMainThreadAsync(() => ReviewPendingApprovalsAsync());
    }

    private async void OnCodingQuestionsRequested(object? Sender, EventArgs Args) =>
        await MainThread.InvokeOnMainThreadAsync(OpenRequestedCodingQuestionsAsync);

    private Task OpenRequestedCodingQuestionsAsync()
    {
        if (!_isVisible || !_hasAppeared || _chatBusy || !_chat.IsOwner || !_notificationRoutingService.TakeCodingQuestionsRequest())
            return Task.CompletedTask;
        if (Navigation.NavigationStack.LastOrDefault()?.Title == "Outstanding questions") return Task.CompletedTask;
        return Navigation.PushAsync(new WorkspacePage(_chat, "Outstanding questions"));
    }

    private async Task ReviewPendingApprovalsAsync(bool ShowEmpty = false)
    {
        if (!_isVisible || !_hasAppeared || _isReviewingApproval) return;
        _isReviewingApproval = true;
        try
        {
            var pendingApproval = _notificationRoutingService.GetNextPendingApproval(_viewModel.SavedProfileId);
            if (pendingApproval is null && ShowEmpty)
                await VisiblePage.DisplayAlertAsync("No pending request", "No active approval request has been received for this profile.", "OK");

            while (_isVisible && pendingApproval is not null)
            {
                // Push data is a navigation hint. Review authoritative server details before deciding.
                var approval = await _viewModel.GetApprovalAsync(pendingApproval.ApprovalId);
                if (!_isVisible) return;
                if (approval is null)
                {
                    await VisiblePage.DisplayAlertAsync("Approval unavailable", "Could not load this request for your saved profile. Check your connection and try Approve Pending again.", "OK");
                    return;
                }
                if (approval.Status != "pending" || approval.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    _notificationRoutingService.Remove(approval.ApprovalId);
                    await VisiblePage.DisplayAlertAsync("Request closed", "This approval has expired or already been decided.", "OK");
                }
                else
                {
                    var choice = await VisiblePage.DisplayActionSheetAsync(
                        $"Agent approval\nTool: {approval.ToolName}\nAction: {approval.ActionSummary}\nRequested by: {approval.RequestedBy}\nExpires: {approval.ExpiresAt.ToLocalTime():g}",
                        "Later", null, "Approve", "Deny");
                    if (choice is not ("Approve" or "Deny")) return;
                    if (!string.Equals(approval.ProfileId, _viewModel.SavedProfileId, StringComparison.OrdinalIgnoreCase)) return;
                    var approved = choice == "Approve";
                    var result = await _viewModel.SubmitApprovalDecisionAsync(approval.ApprovalId, approved,
                        approved ? "Approved from Android app" : "Denied from Android app", _viewModel.SavedProfileId);
                    if (!result.IsSuccess)
                    {
                        await VisiblePage.DisplayAlertAsync("Decision not confirmed", result.Error, "OK");
                        return;
                    }
                    _notificationRoutingService.Remove(approval.ApprovalId);
                    await VisiblePage.DisplayAlertAsync("Decision submitted", approved ? "Approval sent" : "Denial sent", "OK");
                }
                pendingApproval = _notificationRoutingService.GetNextPendingApproval(_viewModel.SavedProfileId);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            await VisiblePage.DisplayAlertAsync("Approval unavailable", "Could not reach the server. Your request is still pending; try Approve Pending when connected.", "OK");
        }
        finally { _isReviewingApproval = false; }
    }

    private View BuildContent()
    {
        var header = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = Color.FromArgb("#0f172a"),
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Padding = new Thickness(12, 10),
            Content = new VerticalStackLayout
            {
                Spacing = 2,
                Children =
                {
                    new Label { Text = "PersonalAgent Mobile", FontSize = 18, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#f8fafc") },
                    new Label { Text = "Device and connection settings", FontSize = 12, TextColor = Color.FromArgb("#cbd5e1") }
                }
            }
        };

        var registerButton = new Button
        {
            Text = "Register Device",
            BackgroundColor = Color.FromArgb("#334155"),
            TextColor = Color.FromArgb("#f8fafc"),
            CornerRadius = 10,
            Padding = new Thickness(14, 10),
            FontSize = 13
        };
        registerButton.Clicked += OnRegisterDeviceClicked;

        var saveProfileButton = new Button
        {
            Text = "Save Profile",
            BackgroundColor = Color.FromArgb("#1d4ed8"),
            TextColor = Color.FromArgb("#eff6ff"),
            CornerRadius = 10,
            Padding = new Thickness(14, 10),
            FontSize = 13
        };
        saveProfileButton.Clicked += OnSaveProfileClicked;

        var profileEntry = new Entry
        {
            Placeholder = "Profile id (e.g. local-owner)",
            BackgroundColor = Colors.White,
            TextColor = Color.FromArgb("#0f172a")
        };
        profileEntry.SetBinding(Entry.TextProperty, nameof(MainViewModel.ProfileId), mode: BindingMode.TwoWay);

        var apiUrlEntry = new Entry
        {
            Placeholder = "API base URL (https://api.example.com)",
            BackgroundColor = Colors.White,
            TextColor = Color.FromArgb("#0f172a")
        };
        apiUrlEntry.SetBinding(Entry.TextProperty, nameof(MainViewModel.ApiBaseUrl), mode: BindingMode.TwoWay);

        var webUrlEntry = new Entry
        {
            Placeholder = "Chat service URL (https://app.example.com)",
            BackgroundColor = Colors.White,
            TextColor = Color.FromArgb("#0f172a")
        };
        webUrlEntry.SetBinding(Entry.TextProperty, nameof(MainViewModel.WebAppUrl), mode: BindingMode.TwoWay);

        var apiKeyEntry = new Entry
        {
            Placeholder = "Internal API key",
            IsPassword = true,
            BackgroundColor = Colors.White,
            TextColor = Color.FromArgb("#0f172a")
        };
        apiKeyEntry.SetBinding(Entry.TextProperty, nameof(MainViewModel.InternalApiKey), mode: BindingMode.TwoWay);

        var saveConnectionButton = new Button
        {
            Text = "Save Connection",
            BackgroundColor = Color.FromArgb("#7c3aed"),
            TextColor = Color.FromArgb("#f5f3ff"),
            CornerRadius = 10,
            Padding = new Thickness(14, 10),
            FontSize = 13
        };
        saveConnectionButton.Clicked += OnSaveConnectionClicked;

        var statusLabel = new Label
        {
            VerticalOptions = LayoutOptions.Center,
            TextColor = Color.FromArgb("#475569"),
            LineBreakMode = LineBreakMode.TailTruncation
        };
        statusLabel.SetBinding(Label.TextProperty, nameof(MainViewModel.StatusMessage));

        var profileRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 8
        };
        profileRow.Add(profileEntry, 0);
        profileRow.Add(saveProfileButton, 1);

        var row2 = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star }
            },
            ColumnSpacing = 8
        };
        row2.Add(registerButton, 0);
        row2.Add(statusLabel, 1);

        var approvalStatusLabel = new Label
        {
            VerticalOptions = LayoutOptions.Center,
            TextColor = Color.FromArgb("#334155"),
            FontAttributes = FontAttributes.Bold,
            LineBreakMode = LineBreakMode.TailTruncation
        };
        approvalStatusLabel.SetBinding(Label.TextProperty, nameof(MainViewModel.LastApprovalStatus), stringFormat: "Last approval: {0}");

        return new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(12, 10, 12, 8),
                Spacing = 10,
                Children =
                {
                    header,
                    profileRow,
                    apiUrlEntry,
                    webUrlEntry,
                    apiKeyEntry,
                    saveConnectionButton,
                    row2,
                    approvalStatusLabel
                }
            }
        };
    }
}
