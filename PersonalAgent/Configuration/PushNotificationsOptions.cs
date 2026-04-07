namespace PersonalAgent.Configuration;

internal record PushNotificationsOptions
{
    public const string SectionName = "PushNotifications";

    public bool Enabled { get; set; } = false;
    public string FirebaseProjectId { get; set; } = string.Empty;
    public string ServiceAccountJson { get; set; } = string.Empty;
    public string ServiceAccountJsonBase64 { get; set; } = string.Empty;
    public string ServiceAccountPath { get; set; } = string.Empty;
    public string AndroidChannelId { get; set; } = "agent-approval-high";
}
