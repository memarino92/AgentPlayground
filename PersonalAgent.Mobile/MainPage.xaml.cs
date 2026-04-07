using PersonalAgent.Mobile.Models;
using PersonalAgent.Mobile.Services;
using PersonalAgent.Mobile.ViewModels;
using Microsoft.Maui.Controls.Shapes;

namespace PersonalAgent.Mobile;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;
    private readonly NotificationRoutingService _notificationRoutingService;
    private readonly WebView _agentWebView;

    public MainPage(MainViewModel viewModel, NotificationRoutingService notificationRoutingService)
    {
        _viewModel = viewModel;
        _notificationRoutingService = notificationRoutingService;
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

    private async void OnSaveConnectionClicked(object? sender, EventArgs e)
    {
        await _viewModel.SaveConnectionSettingsAsync();
        await DisplayAlertAsync("Connection", _viewModel.StatusMessage, "OK");
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
                    apiUrlEntry,
                    webUrlEntry,
                    apiKeyEntry,
                    saveConnectionButton,
                    row1,
                    row2,
                    approvalStatusLabel,
                    new Label { Text = "Web Preview", FontSize = 12, TextColor = Color.FromArgb("#64748b") },
                    webFrame
                }
            }
        };
    }
}
