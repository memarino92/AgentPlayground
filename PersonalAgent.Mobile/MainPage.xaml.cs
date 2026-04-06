using PersonalAgent.Mobile.Models;
using PersonalAgent.Mobile.Services;
using PersonalAgent.Mobile.ViewModels;

namespace PersonalAgent.Mobile;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;
    private readonly NotificationRoutingService _notificationRoutingService;

    public MainPage(MainViewModel viewModel, NotificationRoutingService notificationRoutingService)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _notificationRoutingService = notificationRoutingService;
        BindingContext = _viewModel;
        _notificationRoutingService.PendingApprovalReceived += OnPendingApprovalReceived;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.InitializeAsync();
    }

    private void OnReloadClicked(object? sender, EventArgs e)
    {
        if (AgentWebView.Source is not UrlWebViewSource source) return;
        AgentWebView.Source = new UrlWebViewSource { Url = source.Url };
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
        await _viewModel.TryInjectProfileIntoWebViewAsync(AgentWebView);
    }
}
