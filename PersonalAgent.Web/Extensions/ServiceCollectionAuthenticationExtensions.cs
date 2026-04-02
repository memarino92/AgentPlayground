using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using System.Security.Claims;
using System.Text.Json;

namespace PersonalAgent.Web.Extensions;

internal static class ServiceCollectionAuthenticationExtensions
{
    public static IServiceCollection AddGitHubAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = "GitHub";
        })
        .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddOAuth("GitHub", options =>
        {
            var authConfig = configuration.GetSection("Authentication:Schemes:GitHub");
            var githubClientId = Environment.GetEnvironmentVariable("GITHUB_CLIENT_ID") ?? authConfig["ClientId"];
            var githubClientSecret = Environment.GetEnvironmentVariable("GITHUB_CLIENT_SECRET") ?? authConfig["ClientSecret"];
            var callbackPath = Environment.GetEnvironmentVariable("GITHUB_CALLBACK_PATH") ?? authConfig["CallbackPath"] ?? "/signin-github";
            var allowedUsersString = Environment.GetEnvironmentVariable("GITHUB_ALLOWED_USERS") ?? authConfig["AllowedUsers"] ?? string.Empty;

            options.ClientId = githubClientId ?? throw new InvalidOperationException("Missing GITHUB_CLIENT_ID or Authentication:Schemes:GitHub:ClientId");
            options.ClientSecret = githubClientSecret ?? throw new InvalidOperationException("Missing GITHUB_CLIENT_SECRET or Authentication:Schemes:GitHub:ClientSecret");

            options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
            options.TokenEndpoint = "https://github.com/login/oauth/access_token";
            options.UserInformationEndpoint = "https://api.github.com/user";

            options.Scope.Add("user:email");
            options.CallbackPath = callbackPath;
            options.SaveTokens = true;
            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;

            var allowedUsers = allowedUsersString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

            options.Events = new OAuthEvents
            {
                OnCreatingTicket = async context =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, options.UserInformationEndpoint);
                    request.Headers.Add("Authorization", $"Bearer {context.AccessToken}");
                    request.Headers.Add("User-Agent", "PersonalAgent");
                    request.Headers.Add("Accept", "application/vnd.github.v3+json");

                    using var response = await context.Backchannel.SendAsync(request);
                    if (!response.IsSuccessStatusCode) return;

                    using var content = await response.Content.ReadAsStreamAsync();
                    using var jsonDocument = await JsonDocument.ParseAsync(content);
                    var root = jsonDocument.RootElement;

                    if (!root.TryGetProperty("login", out var loginElement))
                    {
                        context.Fail("Unable to retrieve GitHub login");
                        return;
                    }

                    var githubLogin = loginElement.GetString();
                    if (string.IsNullOrEmpty(githubLogin))
                    {
                        context.Fail("GitHub login is empty");
                        return;
                    }

                    if (allowedUsers.Count > 0 && !allowedUsers.Contains(githubLogin))
                    {
                        context.Fail($"GitHub user '{githubLogin}' is not authorized to access this application");
                        return;
                    }

                    context.Identity?.AddClaim(new Claim(ClaimTypes.NameIdentifier, root.GetProperty("id").ToString()));

                    if (root.TryGetProperty("name", out var name) && !string.IsNullOrEmpty(name.GetString()))
                        context.Identity?.AddClaim(new Claim(ClaimTypes.Name, name.GetString()!));

                    context.Identity?.AddClaim(new Claim("urn:github:login", githubLogin));

                    if (root.TryGetProperty("email", out var email) && !string.IsNullOrEmpty(email.GetString()))
                        context.Identity?.AddClaim(new Claim(ClaimTypes.Email, email.GetString()!));

                    if (root.TryGetProperty("html_url", out var url))
                        context.Identity?.AddClaim(new Claim("urn:github:url", url.GetString()!));

                    if (root.TryGetProperty("avatar_url", out var avatar))
                        context.Identity?.AddClaim(new Claim("urn:github:avatar", avatar.GetString()!));
                }
            };
        });

        services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options =>
        {
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        });

        services.AddAuthorization();
        return services;
    }
}
