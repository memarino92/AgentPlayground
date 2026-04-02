using Microsoft.AspNetCore.HttpOverrides;
using PersonalAgent.Web.Components;
using PersonalAgent.Web.Endpoints;

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

        app.MapAuthenticationEndpoints();

        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        return app;
    }
}
