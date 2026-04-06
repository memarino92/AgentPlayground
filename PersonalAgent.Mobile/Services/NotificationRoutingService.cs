using PersonalAgent.Mobile.Models;

namespace PersonalAgent.Mobile.Services;

public class NotificationRoutingService
{
    public static NotificationRoutingService? Current { get; private set; }

    private readonly object _syncRoot = new();
    private PendingApprovalNotification? _latestApproval;

    public NotificationRoutingService() => Current = this;

    public event EventHandler<PendingApprovalNotification>? PendingApprovalReceived;

    public void RoutePendingApproval(PendingApprovalNotification notification)
    {
        lock (_syncRoot)
            _latestApproval = notification;

        PendingApprovalReceived?.Invoke(this, notification);
    }

    public PendingApprovalNotification? GetLatestPendingApproval()
    {
        lock (_syncRoot)
            return _latestApproval;
    }
}
