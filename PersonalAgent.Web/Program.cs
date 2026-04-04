using AgentPlayground.Contracts.Messaging;
using AgentPlayground.Contracts.Hosting;
using Microsoft.AspNetCore.DataProtection;
using PersonalAgent.Web.Extensions;
using System.IO;

var builder = WebApplication.CreateBuilder(args);
builder.ConfigurePlatformHosting();
if (builder.Environment.IsDevelopment()) builder.Configuration.AddUserSecrets<Program>();
var keyRingPath = Path.Combine(builder.Environment.ContentRootPath, "keys");
Directory.CreateDirectory(keyRingPath);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyRingPath)).SetApplicationName("PersonalAgent.Web");
builder.Services.AddPersonalAgentApiClient(builder.Configuration);
builder.Services.AddGitHubAuthentication(builder.Configuration);
builder.Services.AddPersonalAgentWebMessaging(builder.Configuration);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();
app.UsePersonalAgentWebPipeline();
app.Run();
