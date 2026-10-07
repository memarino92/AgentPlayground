using PersonalAgent.Mobile.Models;

namespace PersonalAgent.Mobile.Services;

public class NotificationRoutingService(TimeProvider? Clock = null)
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<Guid, PendingApprovalNotification> _pendingApprovals = [];
    private readonly TimeProvider _clock = Clock ?? TimeProvider.System;
    private Guid? _reviewFirstId;

    public event EventHandler<PendingApprovalNotification>? PendingApprovalReceived;

    public void RoutePendingApproval(PendingApprovalNotification Notification, bool RequestReview = false)
    {
        if (Notification.ExpiresAt <= _clock.GetUtcNow()) return;
        lock (_syncRoot)
        {
            if (!_pendingApprovals.TryAdd(Notification.ApprovalId, Notification) && !RequestReview) return;
            if (RequestReview) _reviewFirstId = Notification.ApprovalId;
        }
        PendingApprovalReceived?.Invoke(this, Notification);
    }

    public PendingApprovalNotification? GetNextPendingApproval(string ProfileId)
    {
        lock (_syncRoot)
        {
            foreach (var Id in _pendingApprovals.Where(Item => Item.Value.ExpiresAt <= _clock.GetUtcNow()).Select(Item => Item.Key).ToArray())
                _pendingApprovals.Remove(Id);
            if (_reviewFirstId is { } ReviewId && _pendingApprovals.TryGetValue(ReviewId, out var Tapped) &&
                string.Equals(Tapped.ProfileId, ProfileId, StringComparison.OrdinalIgnoreCase)) return Tapped;
            return _pendingApprovals.Values.FirstOrDefault(Item => string.Equals(Item.ProfileId, ProfileId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Remove(Guid ApprovalId)
    {
        lock (_syncRoot)
        {
            _pendingApprovals.Remove(ApprovalId);
            if (_reviewFirstId == ApprovalId) _reviewFirstId = null;
        }
    }
}
