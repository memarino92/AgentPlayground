using Microsoft.AspNetCore.HttpOverrides;

namespace PersonalAgent.Extensions;

internal static class WebApplicationExtensions
{
    public static WebApplicationBuilder ConfigurePlatformHosting(this WebApplicationBuilder builder)
    {
        var port = Environment.GetEnvironmentVariable("PORT");
        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (string.IsNullOrWhiteSpace(urls) && int.TryParse(port, out var parsedPort))
            builder.WebHost.UseUrls($"http://0.0.0.0:{parsedPort}");

        return builder;
    }

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
