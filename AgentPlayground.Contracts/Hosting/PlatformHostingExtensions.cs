using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace AgentPlayground.Contracts.Hosting;

public static class PlatformHostingExtensions
{
    public static WebApplicationBuilder ConfigurePlatformHosting(this WebApplicationBuilder builder)
    {
        var port = Environment.GetEnvironmentVariable("PORT");
        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (string.IsNullOrWhiteSpace(urls) && int.TryParse(port, out var parsedPort))
            builder.WebHost.UseUrls($"http://0.0.0.0:{parsedPort}");

        return builder;
    }
}
