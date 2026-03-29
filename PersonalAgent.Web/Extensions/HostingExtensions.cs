namespace PersonalAgent.Web.Extensions;

internal static class HostingExtensions
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
