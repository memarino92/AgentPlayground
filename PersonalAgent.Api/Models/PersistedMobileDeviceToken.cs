namespace PersonalAgent.Api.Models;

internal record PersistedMobileDeviceToken(string DeviceId, string Platform, string PushToken, string? AppVersion, DateTimeOffset RegisteredAt, DateTimeOffset LastSeenAt);
