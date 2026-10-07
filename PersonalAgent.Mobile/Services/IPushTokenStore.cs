namespace PersonalAgent.Mobile.Services;

public interface IPushTokenStore
{
    string? Read();
    void Write(string Token);
}
