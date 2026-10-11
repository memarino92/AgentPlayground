using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PersonalAgent.Integrations.ChatGpt;

namespace PersonalAgent.Api.Tests.Services;

internal sealed class ChatGptTestTransport : HttpMessageHandler
{
    private readonly RSA Rsa = RSA.Create(2048);
    public int Grants;
    public int Refreshes;
    public Dictionary<string, string>? LastGrant;
    public string Subject = "account-subject";
    public string Audience = "oaiapp_test";
    public string Nonce = "nonce";
    public string GrantedScope = ChatGptOAuth.Scope;
    public string TokenType = "Bearer";
    public bool RevocationWorks = true;
    public bool ExpiredIdentity;
    public string IdentityIssuer = ChatGptOAuth.Issuer;

    public string CreateIdentity(string? NonceValue = null, string? AudienceValue = null, string? SubjectValue = null)
        => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = IdentityIssuer, Audience = AudienceValue ?? Audience, Subject = null,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = SubjectValue ?? Subject, ["email"] = "same@example.test", ["nonce"] = NonceValue ?? Nonce
            },
            IssuedAt = DateTime.UtcNow.AddMinutes(-5), NotBefore = DateTime.UtcNow.AddMinutes(-5),
            Expires = ExpiredIdentity ? DateTime.UtcNow.AddMinutes(-2) : DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(Rsa) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256)
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken Token)
    {
        if (Request.RequestUri!.AbsolutePath == "/.well-known/openid-configuration")
            return Ok(new
            {
                issuer = ChatGptOAuth.Issuer, authorization_endpoint = ChatGptOAuth.Issuer + "/api/accounts/authorize",
                token_endpoint = ChatGptOAuth.Issuer + "/api/accounts/oauth/token", jwks_uri = ChatGptOAuth.Issuer + "/jwks",
                revocation_endpoint = ChatGptOAuth.Issuer + "/revoke", id_token_signing_alg_values_supported = new[] { "RS256" }
            });
        if (Request.RequestUri.AbsolutePath == "/jwks")
        {
            var parameters = Rsa.ExportParameters(false);
            return Ok(new { keys = new[] { new { kty = "RSA", kid = "test-key", alg = "RS256", use = "sig",
                n = Base64UrlEncoder.Encode(parameters.Modulus), e = Base64UrlEncoder.Encode(parameters.Exponent) } } });
        }
        if (Request.RequestUri.AbsolutePath == "/revoke") return new(RevocationWorks ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable);
        if (Request.RequestUri.AbsolutePath == "/api/accounts/oauth/token")
        {
            Grants++;
            var form = await Request.Content!.ReadAsStringAsync(Token);
            LastGrant = form.Split('&').Select(Value => Value.Split('=', 2)).ToDictionary(Value => Uri.UnescapeDataString(Value[0]), Value => Uri.UnescapeDataString(Value[1].Replace('+', ' ')));
            if (LastGrant["grant_type"] == "refresh_token")
            {
                Interlocked.Increment(ref Refreshes);
                await Task.Delay(50, Token);
            }
            return Ok(new { access_token = "new-access", refresh_token = "rotated-refresh", token_type = TokenType,
                expires_in = 3600, scope = GrantedScope, id_token = CreateIdentity() });
        }
        return new(HttpStatusCode.NotFound);
    }
    private static HttpResponseMessage Ok(object Value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(Value) };
    protected override void Dispose(bool Disposing) { if (Disposing) Rsa.Dispose(); base.Dispose(Disposing); }
}
