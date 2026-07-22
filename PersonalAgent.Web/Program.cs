using AgentPlayground.Contracts.Hosting;
using MudBlazor.Services;
using PersonalAgent.Web.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.ConfigurePlatformHosting();
if (builder.Environment.IsDevelopment()) builder.Configuration.AddUserSecrets<Program>();
builder.Services.AddPostgresDataProtection(builder.Configuration);
builder.Services.AddPersonalAgentApiClient(builder.Configuration);
builder.Services.AddGitHubAuthentication(builder.Configuration);
builder.Services.AddPersonalAgentWebMessaging(builder.Configuration);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddMudServices();

var app = builder.Build();
app.UsePersonalAgentWebPipeline();
app.Run();
