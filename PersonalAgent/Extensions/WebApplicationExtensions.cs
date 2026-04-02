using Microsoft.AspNetCore.HttpOverrides;

namespace PersonalAgent.Extensions;

internal static class WebApplicationExtensions
{
    public static WebApplication UsePersonalAgentPipeline(this WebApplication app)
    {
        var forwardedHeadersOptions = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto
        };

        forwardedHeadersOptions.KnownIPNetworks.Clear();
        forwardedHeadersOptions.KnownProxies.Clear();

        app.UseForwardedHeaders(forwardedHeadersOptions);
        app.UseHttpsRedirection();
        app.UseCors();
        app.UseRateLimiter();

        return app;
    }
}
