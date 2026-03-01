using PersonalAgent.Web.Components;
using PersonalAgent.Web.Services;
using PersonalAgent.Web.Endpoints;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using System.Security.Claims;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Authentication
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = "GitHub";
})
.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme)
.AddOAuth("GitHub", options =>
{
    var authConfig = builder.Configuration.GetSection("Authentication:Schemes:GitHub");

    options.ClientId = authConfig["ClientId"] ?? throw new InvalidOperationException("Missing GITHUB_CLIENT_ID");
    options.ClientSecret = authConfig["ClientSecret"] ?? throw new InvalidOperationException("Missing GITHUB_CLIENT_SECRET");

    options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
    options.TokenEndpoint = "https://github.com/login/oauth/access_token";
    options.UserInformationEndpoint = "https://api.github.com/user";

    options.Scope.Add("user:email");
    options.CallbackPath = authConfig["CallbackPath"] ?? "/signin-github";
    options.SaveTokens = true;

    // Get allowed users from config (comma-separated)
    var allowedUsersString = authConfig["AllowedUsers"] ?? "";
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
            if (response.IsSuccessStatusCode)
            {
                using var content = await response.Content.ReadAsStreamAsync();
                using var jsonDocument = await JsonDocument.ParseAsync(content);
                var root = jsonDocument.RootElement;

                // Get GitHub login username
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

                // Check if user is allowed (if AllowedUsers is configured)
                if (allowedUsers.Count > 0 && !allowedUsers.Contains(githubLogin))
                {
                    context.Fail($"GitHub user '{githubLogin}' is not authorized to access this application");
                    return;
                }

                // Map GitHub user claims
                context.Identity?.AddClaim(new Claim(ClaimTypes.NameIdentifier, root.GetProperty("id").ToString()));

                if (root.TryGetProperty("name", out var name) && !string.IsNullOrEmpty(name.GetString()))
                    context.Identity?.AddClaim(new Claim(ClaimTypes.Name, name.GetString()!));

                context.Identity?.AddClaim(new Claim("urn:github:login", githubLogin!));

                if (root.TryGetProperty("email", out var email) && !string.IsNullOrEmpty(email.GetString()))
                    context.Identity?.AddClaim(new Claim(ClaimTypes.Email, email.GetString()!));

                if (root.TryGetProperty("html_url", out var url))
                    context.Identity?.AddClaim(new Claim("urn:github:url", url.GetString()!));

                if (root.TryGetProperty("avatar_url", out var avatar))
                    context.Identity?.AddClaim(new Claim("urn:github:avatar", avatar.GetString()!));
            }
        }
    };
});

builder.Services.AddAuthorization();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Service discovery for PersonalAgent API
var personalAgentApiUri = builder.Configuration["services:personalagent-api:http:0"]
    ?? "http://localhost:5100";

builder.Services.AddHttpClient<PersonalAgentClient>(client =>
{
    client.BaseAddress = new Uri(personalAgentApiUri);
    client.Timeout = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.UseAuthentication();
app.UseAuthorization();

app.MapAuthenticationEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
