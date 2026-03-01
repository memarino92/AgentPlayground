using PersonalAgent.Web.Components;
using PersonalAgent.Web.Services;

var builder = WebApplication.CreateBuilder(args);

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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
