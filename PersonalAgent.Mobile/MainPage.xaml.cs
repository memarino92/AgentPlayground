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

    private async void OnRegisterDeviceClicked(object? sender, EventArgs e) => await _viewModel.RegisterDeviceAsync();

    private async void OnRequestTestApprovalClicked(object? sender, EventArgs e)
    {
        var approvalId = await _viewModel.RequestTestApprovalAsync();
        if (string.IsNullOrWhiteSpace(approvalId))
        {
            await DisplayAlertAsync("Request failed", "Could not create test approval request.", "OK");
            return;
        }

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

        var statusLabel = new Label
        {
            VerticalOptions = LayoutOptions.Center,
            TextColor = Color.FromArgb("#475569"),
            LineBreakMode = LineBreakMode.TailTruncation
        };
        statusLabel.SetBinding(Label.TextProperty, nameof(MainViewModel.StatusMessage));

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
                    row1,
                    requestButton,
                    row2,
                    new Label { Text = "Web Preview", FontSize = 12, TextColor = Color.FromArgb("#64748b") },
                    webFrame
                }
            }
        };
    }
}
