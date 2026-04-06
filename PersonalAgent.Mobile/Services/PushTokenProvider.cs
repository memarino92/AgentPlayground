using Microsoft.Extensions.Logging;

namespace PersonalAgent.Mobile.Services;

public class PushTokenProvider(ILogger<PushTokenProvider> logger) : IPushTokenProvider
{
    private string? _cachedToken;

    public Task<string?> GetPushTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_cachedToken)) return Task.FromResult<string?>(_cachedToken);

        _cachedToken = Preferences.Default.Get("PushToken", string.Empty);
        if (string.IsNullOrWhiteSpace(_cachedToken))
        {
            _cachedToken = $"placeholder-{Guid.NewGuid():N}";
            Preferences.Default.Set("PushToken", _cachedToken);
            logger.LogInformation("Generated placeholder push token for local development");
        }

        return Task.FromResult<string?>(_cachedToken);
    }
}
