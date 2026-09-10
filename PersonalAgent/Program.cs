using AgentPlayground.Contracts.Hosting;
using AgentPlayground.Contracts.Configuration;
using PersonalAgent.Endpoints;
using PersonalAgent.Extensions;
using PersonalAgent.Development;

if (await SyntheticEnvironment.RunHealthProbeAsync(args)) return;

var builder = WebApplication.CreateBuilder(args);
builder.ConfigurePlatformHosting();
if (builder.Environment.IsDevelopment()) builder.Configuration.AddUserSecrets<Program>();
var syntheticDemo = SyntheticEnvironment.IsEnabled(builder.Configuration, builder.Environment);
if (syntheticDemo) await SyntheticConfiguration.InitializeAsync();
builder.Configuration.AddPostgresConfiguration("Api");
builder.Services.AddPersonalAgentServices(builder.Configuration);
if (syntheticDemo) builder.Services.AddSyntheticServices();

var app = builder.Build();
app.UsePersonalAgentPipeline();
app.MapPersonalAgentEndpoints();
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));

app.Run();
