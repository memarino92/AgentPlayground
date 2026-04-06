namespace PersonalAgent.Mobile.Services;

public interface IPushTokenProvider
{
    Task<string?> GetPushTokenAsync(CancellationToken cancellationToken = default);
}
