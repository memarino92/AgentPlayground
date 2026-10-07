using PersonalAgent.Mobile.Services;
using Xunit;

namespace PersonalAgent.Mobile.Tests;

public class PushTokenProviderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("placeholder-old-development-token")]
    public async Task MissingOrPlaceholderTokensAreNotRegistered(string? Token)
    {
        var Store = new TokenStore { Token = Token };
        Assert.Null(await new PushTokenProvider(Store).GetPushTokenAsync());
        Assert.Equal(Token, Store.Token);
    }

    [Fact]
    public async Task RenewalPersistsTheRealTokenAndNotifiesRegistrationOncePerChange()
    {
        var Store = new TokenStore { Token = "old-fcm-token" };
        var Provider = new PushTokenProvider(Store);
        var Updates = new List<string>();
        Provider.TokenUpdated += (_, Token) => Updates.Add(Token);
        Assert.Equal("old-fcm-token", await Provider.GetPushTokenAsync());
        Assert.Empty(Updates);
        Provider.UpdateToken("new-fcm-token");
        Provider.UpdateToken("new-fcm-token");
        Provider.UpdateToken("placeholder-invalid");
        Assert.Equal("new-fcm-token", Store.Token);
        Assert.Equal("new-fcm-token", await Provider.GetPushTokenAsync());
        Assert.Equal(new[] { "new-fcm-token" }, Updates);
    }

    private sealed class TokenStore : IPushTokenStore
    {
        public string? Token { get; set; }
        public string? Read() => Token;
        public void Write(string Value) => Token = Value;
    }
}
