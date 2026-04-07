using PersonalAgent.Mobile.Models;
using PersonalAgent.Mobile.Services;
using PersonalAgent.Mobile.ViewModels;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Extensions.Options;
using PersonalAgent.Mobile.Configuration;

namespace PersonalAgent.Mobile;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;
    private readonly NotificationRoutingService _notificationRoutingService;
    private readonly WebView _agentWebView;
    private readonly MobileAppOptions _options;

    public MainPage(MainViewModel viewModel, NotificationRoutingService notificationRoutingService, IOptions<MobileAppOptions> options)
    {
        _viewModel = viewModel;
        _notificationRoutingService = notificationRoutingService;
        _options = options.Value;
        BindingContext = _viewModel;
        _notificationRoutingService.PendingApprovalReceived += OnPendingApprovalReceived;

        _agentWebView = new WebView();
        _agentWebView.SetBinding(WebView.SourceProperty, nameof(MainViewModel.WebAppUrl));
        _agentWebView.Navigated += OnWebViewNavigated;

        Content = BuildContent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.InitializeAsync();
    }

    private void OnReloadClicked(object? sender, EventArgs e)
    {
        if (_agentWebView.Source is not UrlWebViewSource source) return;
        _agentWebView.Source = new UrlWebViewSource { Url = source.Url };
    }

    private async void OnOpenApprovalClicked(object? sender, EventArgs e) => await OpenApprovalSheetAsync();

    private async void OnRegisterDeviceClicked(object? sender, EventArgs e)
    {
        await _viewModel.RegisterDeviceAsync();
        await DisplayAlertAsync("Register Device", $"{_viewModel.StatusMessage}\nAPI: {_viewModel.GetApiBaseUrl()}", "OK");
    }

    private async void OnSaveProfileClicked(object? sender, EventArgs e)
    {
        await _viewModel.SaveProfileIdAsync();
        await DisplayAlertAsync("Profile", _viewModel.StatusMessage, "OK");
    }

    private async void OnRequestTestApprovalClicked(object? sender, EventArgs e)
    {
        var approvalId = await _viewModel.RequestTestApprovalAsync();
        if (string.IsNullOrWhiteSpace(approvalId))
        {
            await DisplayAlertAsync("Request failed", "Could not create test approval request.", "OK");
            return;
        }

        if (_options.EnableLocalApprovalShortcut && Guid.TryParse(approvalId, out var parsedApprovalId))
            _notificationRoutingService.RoutePendingApproval(new PendingApprovalNotification(
                parsedApprovalId,
                "mobile-debug",
                "ToolCallGuard",
                "Approve test action from Android companion app",
                DateTimeOffset.UtcNow.AddMinutes(5)));

        await DisplayAlertAsync("2FA Requested", $"Approval ID: {approvalId}", "OK");
    }

    private async void OnPendingApprovalReceived(object? sender, PendingApprovalNotification notification)
    {
        await MainThread.InvokeOnMainThreadAsync(OpenApprovalSheetAsync);
    }

    private async Task OpenApprovalSheetAsync()
    {
        var pendingApproval = _notificationRoutingService.GetLatestPendingApproval();
        if (pendingApproval is null)
        {
            await DisplayAlertAsync("No pending request", "No approval request has been received yet.", "OK");
            return;
        }

        var approved = await DisplayAlertAsync(
            "Agent Approval Required",
            $"Tool: {pendingApproval.ToolName}\nAction: {pendingApproval.ActionSummary}",
            "Approve",
            "Deny");

        var decisionBy = _viewModel.ProfileId;
        if (string.IsNullOrWhiteSpace(decisionBy)) decisionBy = "mobile-user";

        await _viewModel.SubmitApprovalDecisionAsync(
            pendingApproval.ApprovalId,
            approved,
            reason: approved ? "Approved from Android app" : "Denied from Android app",
            decidedBy: decisionBy);

        await DisplayAlertAsync("Decision submitted", approved ? "Approval sent" : "Denial sent", "OK");
    }

    private async void OnWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result is not WebNavigationResult.Success) return;
        await _viewModel.TryInjectProfileIntoWebViewAsync(_agentWebView);
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

        var row1 = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Star }
            },
            ColumnSpacing = 8
        };
        row1.Add(new Button { Text = "Reload", BackgroundColor = Color.FromArgb("#1e293b"), TextColor = Color.FromArgb("#f8fafc"), CornerRadius = 10, Padding = new Thickness(14, 10), FontSize = 13 }, 0);
        row1.Add(new Button { Text = "Approve Pending", BackgroundColor = Color.FromArgb("#166534"), TextColor = Color.FromArgb("#f0fdf4"), CornerRadius = 10, Padding = new Thickness(14, 10), FontSize = 13 }, 1);
        ((Button)row1.Children[0]).Clicked += OnReloadClicked;
        ((Button)row1.Children[1]).Clicked += OnOpenApprovalClicked;

        var requestButton = new Button
        {
            Text = "Request 2FA",
            BackgroundColor = Color.FromArgb("#0f766e"),
            TextColor = Color.FromArgb("#ecfeff"),
            CornerRadius = 10,
            Padding = new Thickness(14, 10),
            FontSize = 13
        };
        requestButton.Clicked += OnRequestTestApprovalClicked;

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
            Placeholder = "Profile id (e.g. memarino92)",
            BackgroundColor = Colors.White,
            TextColor = Color.FromArgb("#0f172a")
        };
        profileEntry.SetBinding(Entry.TextProperty, nameof(MainViewModel.ProfileId), mode: BindingMode.TwoWay);

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

        var refreshStatusButton = new Button
        {
            Text = "Refresh Status",
            BackgroundColor = Color.FromArgb("#475569"),
            TextColor = Color.FromArgb("#f8fafc"),
            CornerRadius = 10,
            Padding = new Thickness(14, 10),
            FontSize = 13
        };
        refreshStatusButton.Clicked += async (_, _) => await _viewModel.PollLastApprovalStatusAsync();

        var approvalStatusLabel = new Label
        {
            VerticalOptions = LayoutOptions.Center,
            TextColor = Color.FromArgb("#334155"),
            FontAttributes = FontAttributes.Bold,
            LineBreakMode = LineBreakMode.TailTruncation
        };
        approvalStatusLabel.SetBinding(Label.TextProperty, nameof(MainViewModel.LastApprovalStatus));

        var row3 = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star }
            },
            ColumnSpacing = 8
        };
        row3.Add(refreshStatusButton, 0);
        row3.Add(approvalStatusLabel, 1);

        var webFrame = new Border
        {
            Stroke = Color.FromArgb("#cbd5e1"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            BackgroundColor = Colors.White,
            Padding = 0,
            HeightRequest = 260,
            VerticalOptions = LayoutOptions.Start,
            Content = _agentWebView
        };

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
                    row1,
                    requestButton,
                    row2,
                    row3,
                    new Label { Text = "Web Preview", FontSize = 12, TextColor = Color.FromArgb("#64748b") },
                    webFrame
                }
            }
        };
    }
}
