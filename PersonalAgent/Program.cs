using AgentPlayground.Contracts.Hosting;
using PersonalAgent.Endpoints;
using PersonalAgent.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.ConfigurePlatformHosting();
if (builder.Environment.IsDevelopment()) builder.Configuration.AddUserSecrets<Program>();
builder.Services.AddPersonalAgentServices(builder.Configuration);

var app = builder.Build();
app.UsePersonalAgentPipeline();
app.MapPersonalAgentEndpoints();

app.Run();
