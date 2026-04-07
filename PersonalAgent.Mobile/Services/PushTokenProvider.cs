using Microsoft.Extensions.Logging;

namespace PersonalAgent.Mobile.Services;

public class PushTokenProvider(ILogger<PushTokenProvider> logger) : IPushTokenProvider
{
    private string? _cachedToken;

    public event EventHandler<string>? TokenUpdated;

    public Task<string?> GetPushTokenAsync(CancellationToken cancellationToken = default)
    {
        var persisted = Preferences.Default.Get("PushToken", string.Empty);
        if (!string.IsNullOrWhiteSpace(persisted))
        {
            if (!string.Equals(_cachedToken, persisted, StringComparison.Ordinal))
            {
                _cachedToken = persisted;
                TokenUpdated?.Invoke(this, persisted);
            }

            return Task.FromResult<string?>(_cachedToken);
        }

        if (string.IsNullOrWhiteSpace(_cachedToken))
        {
            _cachedToken = $"placeholder-{Guid.NewGuid():N}";
            Preferences.Default.Set("PushToken", _cachedToken);
            logger.LogInformation("Generated placeholder push token for local development");
            TokenUpdated?.Invoke(this, _cachedToken);
        }

        return Task.FromResult<string?>(_cachedToken);
    }
}
