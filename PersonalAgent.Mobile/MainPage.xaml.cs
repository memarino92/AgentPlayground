using PersonalAgent.Mobile.Models;
using PersonalAgent.Mobile.Services;
using PersonalAgent.Mobile.ViewModels;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Extensions.Logging;

namespace PersonalAgent.Mobile;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;
    private readonly NotificationRoutingService _notificationRoutingService;
    private readonly WebView _agentWebView;
    private readonly FirebaseCloudMessagingBridge _firebaseBridge;
    private bool _isVisible;
    private bool _isReviewingApproval;
    private bool _hasAppeared;

    public MainPage(MainViewModel viewModel, NotificationRoutingService notificationRoutingService, FirebaseCloudMessagingBridge firebaseBridge, ILogger<MainPage> logger)
    {
        _viewModel = viewModel;
        _notificationRoutingService = notificationRoutingService;
        _firebaseBridge = firebaseBridge;
        BindingContext = _viewModel;
        _notificationRoutingService.PendingApprovalReceived += OnPendingApprovalReceived;

        _agentWebView = new WebView();
        _agentWebView.SetBinding(WebView.SourceProperty, nameof(MainViewModel.WebAppUrl));
        _agentWebView.Navigated += (_, Args) =>
        {
            logger.LogInformation("PersonalAgent website navigation completed: {NavigationResult}", Args.Result);
#if ANDROID && DEBUG
            Android.Util.Log.Info("PersonalAgent.Mobile", $"WebsiteNavigation={Args.Result}");
#endif
        };

        var settingsButton = new Button { Text = "Settings", FontSize = 13 };
        settingsButton.Clicked += async (_, _) =>
        {
            var settingsPage = new ContentPage { Title = "Settings", Content = BuildContent() };
            var closeButton = new ToolbarItem { Text = "Done" };
            closeButton.Clicked += async (_, _) => await Navigation.PopModalAsync();
            settingsPage.ToolbarItems.Add(closeButton);
            await Navigation.PushModalAsync(new NavigationPage(settingsPage));
        };
        var approvalsButton = new Button { Text = "Approvals", FontSize = 13 };
        approvalsButton.Clicked += OnOpenApprovalClicked;
        var toolbar = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)]
        };
        toolbar.Add(approvalsButton, 0);
        toolbar.Add(settingsButton, 1);
        var root = new Grid
        {
            RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star)],
            SafeAreaEdges = SafeAreaEdges.All
        };
        root.Add(toolbar, 0, 0);
        root.Add(_agentWebView, 0, 1);
        Content = root;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _isVisible = true;
        _hasAppeared = true;
        await _viewModel.InitializeAsync();
        await _firebaseBridge.InitializeAsync();
        await ReviewPendingApprovalsAsync();
    }

    protected override void OnDisappearing()
    {
        _isVisible = false;
        base.OnDisappearing();
    }

    public async Task SetActiveAsync(bool Active)
    {
        _isVisible = Active;
        if (Active && _hasAppeared) await ReviewPendingApprovalsAsync();
    }

    private void OnReloadClicked(object? sender, EventArgs e)
    {
        if (_agentWebView.Source is not UrlWebViewSource source) return;
        _agentWebView.Source = new UrlWebViewSource { Url = source.Url };
    }

    private async void OnOpenApprovalClicked(object? sender, EventArgs e) => await ReviewPendingApprovalsAsync(true);

    private async void OnRegisterDeviceClicked(object? sender, EventArgs e)
    {
        await _firebaseBridge.InitializeAsync();
        await _viewModel.RegisterDeviceAsync();
        await DisplayAlertAsync("Register Device", $"{_viewModel.StatusMessage}\nAPI: {_viewModel.GetApiBaseUrl()}", "OK");
    }

    private async void OnSaveProfileClicked(object? sender, EventArgs e)
    {
        await _viewModel.SaveProfileIdAsync();
        await DisplayAlertAsync("Profile", _viewModel.StatusMessage, "OK");
        await ReviewPendingApprovalsAsync();
    }

    private async void OnSaveConnectionClicked(object? sender, EventArgs e)
    {
        await _viewModel.SaveConnectionSettingsAsync();
        await DisplayAlertAsync("Connection", _viewModel.StatusMessage, "OK");
    }

    private async void OnPendingApprovalReceived(object? sender, PendingApprovalNotification notification)
    {
        await MainThread.InvokeOnMainThreadAsync(() => ReviewPendingApprovalsAsync());
    }

    private async Task ReviewPendingApprovalsAsync(bool ShowEmpty = false)
    {
        if (!_isVisible || _isReviewingApproval) return;
        _isReviewingApproval = true;
        try
        {
            var pendingApproval = _notificationRoutingService.GetNextPendingApproval(_viewModel.ProfileId);
            if (pendingApproval is null && ShowEmpty)
                await DisplayAlertAsync("No pending request", "No active approval request has been received for this profile.", "OK");

            while (_isVisible && pendingApproval is not null)
            {
                // Push data is a navigation hint. Review authoritative server details before deciding.
                var approval = await _viewModel.GetApprovalAsync(pendingApproval.ApprovalId);
                if (!_isVisible) return;
                if (approval is null)
                {
                    await DisplayAlertAsync("Approval unavailable", "Could not load this request for your saved profile. Check your connection and try Approve Pending again.", "OK");
                    return;
                }
                if (approval.Status != "pending" || approval.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    _notificationRoutingService.Remove(approval.ApprovalId);
                    await DisplayAlertAsync("Request closed", "This approval has expired or already been decided.", "OK");
                }
                else
                {
                    var choice = await DisplayActionSheetAsync(
                        $"Agent approval\nTool: {approval.ToolName}\nAction: {approval.ActionSummary}\nRequested by: {approval.RequestedBy}\nExpires: {approval.ExpiresAt.ToLocalTime():g}",
                        "Later", null, "Approve", "Deny");
                    if (choice is not ("Approve" or "Deny")) return;
                    if (!string.Equals(approval.ProfileId, _viewModel.ProfileId, StringComparison.OrdinalIgnoreCase)) return;
                    var approved = choice == "Approve";
                    var result = await _viewModel.SubmitApprovalDecisionAsync(approval.ApprovalId, approved,
                        approved ? "Approved from Android app" : "Denied from Android app", _viewModel.ProfileId);
                    if (!result.IsSuccess)
                    {
                        await DisplayAlertAsync("Decision not confirmed", result.Error, "OK");
                        return;
                    }
                    _notificationRoutingService.Remove(approval.ApprovalId);
                    await DisplayAlertAsync("Decision submitted", approved ? "Approval sent" : "Denial sent", "OK");
                }
                pendingApproval = _notificationRoutingService.GetNextPendingApproval(_viewModel.ProfileId);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            await DisplayAlertAsync("Approval unavailable", "Could not reach the server. Your request is still pending; try Approve Pending when connected.", "OK");
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
                    new Label { Text = "2FA gateway + web companion", FontSize = 12, TextColor = Color.FromArgb("#cbd5e1") }
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
            Placeholder = "Web URL (https://app.example.com)",
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
