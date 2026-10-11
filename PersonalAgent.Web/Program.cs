using PersonalAgent.Contracts.Hosting;
using PersonalAgent.Integrations;
using PersonalAgent.Contracts.Configuration;
using MudBlazor.Services;
using PersonalAgent.Web.Extensions;
using PersonalAgent.Web.Development;

if (await SyntheticEnvironment.RunHealthProbeAsync(args)) return;

var builder = WebApplication.CreateBuilder(args);
builder.ConfigurePlatformHosting();
if (builder.Environment.IsDevelopment()) builder.Configuration.AddUserSecrets<Program>();
var syntheticDemo = SyntheticEnvironment.IsEnabled(builder.Configuration, builder.Environment);
builder.Configuration.AddPostgresConfiguration("Web");
builder.Services.AddRuntimeIntegrations(builder.Configuration, "Web");
builder.AddApplicationObservability("Web");
builder.Services.AddPostgresDataProtection(builder.Configuration);
builder.Services.AddPersonalAgentApiClient(builder.Configuration);
if (syntheticDemo) builder.Services.AddSyntheticAuthentication();
else builder.Services.AddGitHubAuthentication(builder.Configuration);
builder.Services.AddPersonalAgentWebMessaging(builder.Configuration);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddMudServices();
builder.Services.AddRateLimiter(Options => Options.AddPolicy("chatgpt-pairing", Context =>
    System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        Context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
        {
            PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        })));

var app = builder.Build();
app.UsePersonalAgentWebPipeline();
app.UseRateLimiter();
app.Use(async (Context, Next) =>
{
    if (Context.Request.Path == "/chatgpt/handshake")
    {
        var limit = Context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = 131072;
    }
    await Next(Context);
});
app.MapPost("/chatgpt/handshake", async (HttpContext Context, PersonalAgent.Integrations.ChatGpt.ChatGptHandshake Request,
    PersonalAgent.Web.Services.PersonalAgentClient Client) =>
{
    Context.Response.Headers.CacheControl = "no-store";
    if (Context.Request.ContentLength is > 131072) return Results.StatusCode(413);
    using var response = await Client.RelayChatGptHandshakeAsync(Request, Context.RequestAborted);
    // Return only the protocol body. Never forward cookies or internal API headers.
    return Results.Content(await response.Content.ReadAsStringAsync(Context.RequestAborted),
        "application/json", statusCode: (int)response.StatusCode);
}).RequireRateLimiting("chatgpt-pairing");
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));
app.Run();
