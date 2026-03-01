using Microsoft.AspNetCore.HttpOverrides;

namespace PersonalAgent.Extensions;

internal static class WebApplicationExtensions
{
    public static WebApplication UsePersonalAgentPipeline(this WebApplication app)
    {
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        });
        app.UseHttpsRedirection();
        app.UseCors();
        app.UseRateLimiter();

        return app;
    }
}
