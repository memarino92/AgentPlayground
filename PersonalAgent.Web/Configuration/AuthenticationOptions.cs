namespace PersonalAgent.Web.Configuration;

/// <summary>
/// Configuration options for GitHub OIDC authentication.
/// Set via environment variables: GITHUB_CLIENT_ID and GITHUB_CLIENT_SECRET
/// </summary>
internal record AuthenticationOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Authority { get; set; } = "https://github.com";
    public string Scope { get; set; } = "openid profile email";
    public bool SaveTokens { get; set; } = true;
    public string CallbackPath { get; set; } = "/signin-oidc";
}
