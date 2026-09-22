using PersonalAgent.Contracts.Hosting;
using PersonalAgent.Integrations;
using PersonalAgent.Api.Services;
using PersonalAgent.Contracts.Configuration;
using PersonalAgent.Api.Endpoints;
using PersonalAgent.Api.Extensions;
using PersonalAgent.Api.Development;

if (await SyntheticEnvironment.RunHealthProbeAsync(args)) return;

var builder = WebApplication.CreateBuilder(args);
builder.ConfigurePlatformHosting();
if (builder.Environment.IsDevelopment()) builder.Configuration.AddUserSecrets<Program>();
var syntheticDemo = SyntheticEnvironment.IsEnabled(builder.Configuration, builder.Environment);
if (syntheticDemo) await SyntheticConfiguration.InitializeAsync();
builder.Configuration.AddPostgresConfiguration("Api");
builder.Services.AddPersonalAgentServices(builder.Configuration);
builder.Services.AddRuntimeIntegrations(builder.Configuration, "Api");
builder.AddApplicationObservability("Api");
builder.Services.AddSingleton<IIntegrationSettingsService, IntegrationSettingsService>();
builder.Services.AddSingleton<OtelSettingsService>();
builder.Services.AddSingleton<DatabaseSettingsStore>();
builder.Services.AddSingleton<DatabaseCredentialRuntime>();
builder.Services.AddHostedService<DatabaseCredentialReloadWorker>();
if (syntheticDemo) builder.Services.AddSyntheticServices();
else
{
    builder.Services.AddHostedService<DatabaseChatModelPolicyInitializer>();
    builder.Services.AddHostedService<CoachCheckinSettingsInitializer>();
}

builder.Services.AddHostedService(sp => sp.GetRequiredService<JevRoutingRuntime>());

var app = builder.Build();
app.UsePersonalAgentPipeline();
app.MapPersonalAgentEndpoints();
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));

app.Run();
