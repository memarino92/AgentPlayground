using AgentPlayground.Contracts.Hosting;
using AgentPlayground.Contracts.Configuration;
using MudBlazor.Services;
using PersonalAgent.Web.Extensions;
using PersonalAgent.Web.Development;

if (await SyntheticEnvironment.RunHealthProbeAsync(args)) return;

var builder = WebApplication.CreateBuilder(args);
builder.ConfigurePlatformHosting();
if (builder.Environment.IsDevelopment()) builder.Configuration.AddUserSecrets<Program>();
var syntheticDemo = SyntheticEnvironment.IsEnabled(builder.Configuration, builder.Environment);
builder.Configuration.AddPostgresConfiguration("Web");
builder.Services.AddPostgresDataProtection(builder.Configuration);
builder.Services.AddPersonalAgentApiClient(builder.Configuration);
if (syntheticDemo) builder.Services.AddSyntheticAuthentication();
else builder.Services.AddGitHubAuthentication(builder.Configuration);
builder.Services.AddPersonalAgentWebMessaging(builder.Configuration);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddMudServices();

var app = builder.Build();
app.UsePersonalAgentWebPipeline();
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));
app.Run();
