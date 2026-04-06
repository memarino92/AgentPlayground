namespace PersonalAgent.Mobile.Models;

internal record RegisterMobileDeviceTokenRequest(string ProfileId, string DeviceId, string Platform, string PushToken, string? AppVersion);
