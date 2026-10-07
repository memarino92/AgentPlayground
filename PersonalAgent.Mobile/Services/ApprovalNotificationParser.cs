using System.Globalization;

using PersonalAgent.Mobile.Models;

namespace PersonalAgent.Mobile.Services;

public static class ApprovalNotificationParser
{
    public static PendingApprovalNotification? Parse(IReadOnlyDictionary<string, string>? Data)
    {
        if (Data is null || !Data.TryGetValue("notificationType", out var Type) || Type != "agent-approval") return null;
        if (!Data.TryGetValue("approvalId", out var Id) || !Guid.TryParse(Id, out var ApprovalId) || ApprovalId == Guid.Empty) return null;
        if (!Data.TryGetValue("profileId", out var ProfileId) || string.IsNullOrWhiteSpace(ProfileId)) return null;
        if (!Data.TryGetValue("expiresAt", out var Expiry) || !DateTimeOffset.TryParse(Expiry, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ExpiresAt)) return null;

        return new(ApprovalId, ProfileId, Data.GetValueOrDefault("sessionId", string.Empty),
            Data.GetValueOrDefault("toolName", string.Empty), Data.GetValueOrDefault("actionSummary", string.Empty),
            Data.GetValueOrDefault("requestedBy", string.Empty), ExpiresAt);
    }
}
