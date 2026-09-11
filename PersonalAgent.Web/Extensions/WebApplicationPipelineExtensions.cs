using Microsoft.AspNetCore.HttpOverrides;
using PersonalAgent.Web.Components;
using PersonalAgent.Web.Endpoints;
using AgentPlayground.Contracts.Hosting;
using PersonalAgent.Web.Development;

namespace PersonalAgent.Web.Extensions;

internal static class WebApplicationPipelineExtensions
{
    public static WebApplication UsePersonalAgentWebPipeline(this WebApplication app)
    {
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

        if (SyntheticEnvironment.IsEnabled(app.Configuration, app.Environment)) app.MapSyntheticAuthentication();
        else app.MapAuthenticationEndpoints();

        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
        app.MapCoachAudio();

        return app;
    }
}
