using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace PersonalAgent.Integrations.ChatGpt;

public sealed class ChatGptOAuth(HttpClient Http)
{
    public const string Issuer = "https://auth.openai.com";
    public const string Resource = "https://api.openai.com/v1";
    public const string RedirectUri = "http://127.0.0.1:1455/auth/callback";
    public const string Scope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    private readonly ConfigurationManager<OpenIdConnectConfiguration> Discovery = new(
        Issuer + "/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever(),
        new HttpDocumentRetriever(Http) { RequireHttps = true });

    public static string RandomValue() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    public static bool PlanEnabled(string GrantedScope) => GrantedScope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Contains("chatgpt.tokens.use.direct", StringComparer.Ordinal)
        && GrantedScope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("resource.invoke", StringComparer.Ordinal);

    public static string AuthorizeUrl(ChatGptAuthorization Attempt)
    {
        Dictionary<string, string> values = new()
        {
            ["client_id"] = Attempt.ClientId ?? "dynamic_agent_client", ["ext_agent_host_id"] = Attempt.HostId,
            ["response_type"] = "code", ["redirect_uri"] = RedirectUri, ["scope"] = Scope,
            ["resource"] = Resource, ["state"] = Attempt.State, ["nonce"] = Attempt.Nonce,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(Attempt.Verifier)))
        };
        if (Attempt.ClientId is null) values["agent_name_hint"] = "AgentPlayground";
        if (Attempt.IdTokenHint is not null) values["id_token_hint"] = Attempt.IdTokenHint;
        if (Attempt.EmailHint is not null) values["login_hint"] = Attempt.EmailHint;
        return Issuer + "/api/accounts/authorize?" + string.Join("&", values.Select(Pair =>
            Uri.EscapeDataString(Pair.Key) + "=" + Uri.EscapeDataString(Pair.Value)));
    }

    public async Task<ChatGptCredentials> ExchangeAsync(ChatGptAuthorization Attempt, string State,
        string? Code, string? ClientId, string? Error, CancellationToken Token)
    {
        if (State != Attempt.State) throw new ChatGptException("The sign-in state did not match. Start again in Settings.");
        if (Error is not null) throw new ChatGptException("ChatGPT sign-in was declined. Start again when ready.");
        var issued = Attempt.ClientId ?? ClientId;
        if (string.IsNullOrWhiteSpace(Code) || string.IsNullOrWhiteSpace(issued) || !issued.StartsWith("oaiapp_", StringComparison.Ordinal)
            || Attempt.ClientId is not null && ClientId is not null && Attempt.ClientId != ClientId)
            throw new ChatGptException("ChatGPT did not return the expected registration. Start again.");
        var response = await GrantAsync(new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = issued, ["code"] = Code,
            ["code_verifier"] = Attempt.Verifier, ["redirect_uri"] = RedirectUri, ["resource"] = Resource
        }, Token);
        var identity = await ValidateIdentityAsync(response.IdToken ?? "", issued, Attempt.Nonce, Token);
        return Credentials(response, issued, identity.Subject, identity.Email);
    }

    public async Task<(string Subject, string? Email)> ValidateIdentityAsync(string IdToken, string ClientId, string Nonce, CancellationToken Token)
    {
        var configuration = await Discovery.GetConfigurationAsync(Token);
        if (configuration.Issuer != Issuer) throw new ChatGptException("OpenAI identity discovery returned an unexpected issuer.");
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(IdToken, new TokenValidationParameters
        {
            ValidIssuer = Issuer, ValidAudience = ClientId, IssuerSigningKeys = configuration.SigningKeys,
            ValidateIssuerSigningKey = true, ValidateIssuer = true, ValidateAudience = true,
            ValidateLifetime = true, RequireExpirationTime = true, RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ClockSkew = TimeSpan.FromSeconds(30)
        });
        if (!validation.IsValid) throw new ChatGptException("ChatGPT identity could not be verified. Start a fresh sign-in.");
        var jwt = (JsonWebToken)validation.SecurityToken;
        if (!jwt.TryGetClaim("nonce", out var nonce) || nonce.Value != Nonce || string.IsNullOrWhiteSpace(jwt.Subject))
            throw new ChatGptException("ChatGPT identity did not match the pending sign-in.");
        if (jwt.Audiences.Count() > 1 && (!jwt.TryGetClaim("azp", out var party) || party.Value != ClientId))
            throw new ChatGptException("ChatGPT returned an unexpected authorized party.");
        return (jwt.Subject, jwt.TryGetClaim("email", out var email) ? email.Value : null);
    }

    public async Task<ChatGptCredentials> RefreshAsync(ChatGptCredentials Previous, CancellationToken Token)
    {
        if (string.IsNullOrWhiteSpace(Previous.RefreshToken)) throw new ChatGptException("Reconnect this ChatGPT account in Settings.");
        var response = await GrantAsync(new()
        {
            ["grant_type"] = "refresh_token", ["client_id"] = Previous.ClientId,
            ["refresh_token"] = Previous.RefreshToken, ["resource"] = Resource
        }, Token);
        return Credentials(response, Previous.ClientId, Previous.Subject, Previous.Email) with
        {
            IdToken = Previous.IdToken, RefreshToken = response.RefreshToken ?? Previous.RefreshToken,
            Scope = response.Scope ?? Previous.Scope
        };
    }

    public async Task<bool> RevokeAsync(ChatGptCredentials Credentials, CancellationToken Token)
    {
        if (Credentials.RefreshToken is null) return true;
        var configuration = await Discovery.GetConfigurationAsync(Token);
        if (!Uri.TryCreate(configuration.RevocationEndpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.Host != "auth.openai.com") return false;
        using var response = await Http.PostAsync(uri, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = Credentials.RefreshToken, ["token_type_hint"] = "refresh_token", ["client_id"] = Credentials.ClientId
        }), Token);
        return response.IsSuccessStatusCode;
    }

    private async Task<TokenResponse> GrantAsync(Dictionary<string, string> Values, CancellationToken Token)
    {
        using var response = await Http.PostAsync(Issuer + "/api/accounts/oauth/token", new FormUrlEncodedContent(Values), Token);
        if (!response.IsSuccessStatusCode) throw new ChatGptException("ChatGPT authorization expired or was revoked. Reconnect in Settings.");
        var result = await response.Content.ReadFromJsonAsync<TokenResponse>(Token);
        if (result is null || !string.Equals(result.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(result.AccessToken) || result.ExpiresIn is <= 0 or > 86400)
            throw new ChatGptException("ChatGPT returned an invalid token response.");
        return result;
    }

    private static ChatGptCredentials Credentials(TokenResponse Response, string ClientId, string Subject, string? Email) =>
        new(ClientId, Subject, Email, Response.IdToken ?? "", Response.AccessToken, Response.RefreshToken,
            Response.Scope ?? "", DateTimeOffset.UtcNow.AddSeconds(Response.ExpiresIn));

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("id_token")] string? IdToken,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
