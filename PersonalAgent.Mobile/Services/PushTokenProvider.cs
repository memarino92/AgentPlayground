namespace PersonalAgent.Mobile.Services;

public class PushTokenProvider(IPushTokenStore Store) : IPushTokenProvider
{
    public event EventHandler<string>? TokenUpdated;

    public Task<string?> GetPushTokenAsync(CancellationToken CancellationToken = default)
    {
        CancellationToken.ThrowIfCancellationRequested();
        var Token = Store.Read();
        return Task.FromResult<string?>(IsValid(Token) ? Token : null);
    }

    public void UpdateToken(string Token)
    {
        if (!IsValid(Token) || string.Equals(Store.Read(), Token, StringComparison.Ordinal)) return;
        Store.Write(Token);
        TokenUpdated?.Invoke(this, Token);
    }

    private static bool IsValid(string? Token) => !string.IsNullOrWhiteSpace(Token) && !Token.StartsWith("placeholder-", StringComparison.Ordinal);
}
