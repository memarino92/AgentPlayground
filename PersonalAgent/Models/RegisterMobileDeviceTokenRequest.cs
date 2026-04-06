namespace PersonalAgent.Models;

internal record RegisterMobileDeviceTokenRequest(string ProfileId, string DeviceId, string Platform, string PushToken, string? AppVersion);
