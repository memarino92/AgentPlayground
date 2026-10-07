namespace PersonalAgent.Mobile.Services;

public class PushTokenStore : IPushTokenStore
{
    public string? Read() => Preferences.Default.Get("PushToken", string.Empty);
    public void Write(string Token) => Preferences.Default.Set("PushToken", Token);
}
