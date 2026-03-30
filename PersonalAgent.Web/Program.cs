using AgentPlayground.Contracts.Messaging;
using PersonalAgent.Web.Components;
using PersonalAgent.Web.Configuration;
using PersonalAgent.Web.Extensions;
using PersonalAgent.Web.Services;
using PersonalAgent.Web.Endpoints;
using PersonalAgent.Web.Messaging;
using MassTransit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.HttpOverrides;
using System.Security.Claims;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.ConfigurePlatformHosting();
if (builder.Environment.IsDevelopment()) builder.Configuration.AddUserSecrets<Program>();

builder.Services.AddOptions<PersonalAgentApiOptions>()
    .Configure(opts =>
    {
        builder.Configuration.GetSection(PersonalAgentApiOptions.SectionName).Bind(opts);

        opts.BaseUrl = FirstNonEmpty(
            Environment.GetEnvironmentVariable("PERSONAL_AGENT_API_BASE_URL"),
            builder.Configuration["services:personalagent-api:http:0"],
            opts.BaseUrl);

        opts.InternalApiKey = FirstNonEmpty(
            Environment.GetEnvironmentVariable("PERSONAL_AGENT_INTERNAL_API_KEY"),
            builder.Configuration[$"{PersonalAgentApiOptions.SectionName}:InternalApiKey"],
            builder.Configuration["Security:InternalApiKey"],
            opts.InternalApiKey);
    })
    .Validate(opts => Uri.TryCreate(opts.BaseUrl, UriKind.Absolute, out _), $"{PersonalAgentApiOptions.SectionName}:BaseUrl must be an absolute URI")
    .Validate(opts => !string.IsNullOrWhiteSpace(opts.InternalApiKey), $"{PersonalAgentApiOptions.SectionName}:InternalApiKey is required")
    .ValidateOnStart();

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
    var githubClientId = Environment.GetEnvironmentVariable("GITHUB_CLIENT_ID") ?? authConfig["ClientId"];
    var githubClientSecret = Environment.GetEnvironmentVariable("GITHUB_CLIENT_SECRET") ?? authConfig["ClientSecret"];
    var callbackPath = Environment.GetEnvironmentVariable("GITHUB_CALLBACK_PATH") ?? authConfig["CallbackPath"] ?? "/signin-github";
    var allowedUsersString = Environment.GetEnvironmentVariable("GITHUB_ALLOWED_USERS") ?? authConfig["AllowedUsers"] ?? "";

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
builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options =>
{
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

builder.Services.AddAuthorization();
builder.Services.AddMessagingOptions(builder.Configuration);
builder.Services.AddOptions<SqlTransportOptions>()
    .Configure<IOptions<MessagingOptions>>((sqlOptions, messagingOptions) =>
    {
        sqlOptions.ConnectionString = messagingOptions.Value.ConnectionString;
    })
    .Validate(opts => !string.IsNullOrWhiteSpace(opts.ConnectionString), "SqlTransportOptions:ConnectionString is required")
    .ValidateOnStart();

builder.Services.AddPostgresMigrationHostedService(options =>
{
    options.CreateDatabase = false;
    options.CreateSchema = true;
    options.CreateInfrastructure = false;
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddScoped<ProtectedSessionStorage>();
builder.Services.AddMassTransit(x => x.ConfigureSharedPostgresTransport());
builder.Services.AddScoped<TestEventPublisher>();
builder.Services.AddHttpClient<PersonalAgentClient>((serviceProvider, client) =>
{
    var personalAgentApiOptions = serviceProvider.GetRequiredService<IOptions<PersonalAgentApiOptions>>().Value;

    client.BaseAddress = new Uri(personalAgentApiOptions.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);

    if (!string.IsNullOrWhiteSpace(personalAgentApiOptions.InternalApiKey))
        client.DefaultRequestHeaders.Add("X-Internal-Api-Key", personalAgentApiOptions.InternalApiKey);
});

var app = builder.Build();

var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto
};

forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();

app.UseForwardedHeaders(forwardedHeadersOptions);

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

static string FirstNonEmpty(params string?[] values) =>
    values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
