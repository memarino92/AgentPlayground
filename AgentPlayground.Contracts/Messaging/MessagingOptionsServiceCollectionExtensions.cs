using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text;

namespace AgentPlayground.Contracts.Messaging;

public static class MessagingOptionsServiceCollectionExtensions
{
    public static IServiceCollection AddMessagingOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MessagingOptions>()
            .Configure(opts =>
            {
                configuration.GetSection(MessagingOptions.SectionName).Bind(opts);

                opts.ConnectionString = ResolveConnectionString(configuration, opts.ConnectionString);
                opts.Schema = Environment.GetEnvironmentVariable("MESSAGING_SCHEMA")
                    ?? configuration[$"{MessagingOptions.SectionName}:Schema"]
                    ?? opts.Schema;

                var createInfrastructure = Environment.GetEnvironmentVariable("MESSAGING_CREATE_INFRASTRUCTURE")
                    ?? configuration[$"{MessagingOptions.SectionName}:CreateInfrastructure"];

                if (bool.TryParse(createInfrastructure, out var shouldCreateInfrastructure))
                    opts.CreateInfrastructure = shouldCreateInfrastructure;
            })
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.ConnectionString), $"{MessagingOptions.SectionName}:ConnectionString is required")
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.Schema), $"{MessagingOptions.SectionName}:Schema is required")
            .ValidateOnStart();

        return services;
    }

    private static string ResolveConnectionString(IConfiguration configuration, string configuredValue)
    {
        var connectionString = Environment.GetEnvironmentVariable("MESSAGING_CONNECTION_STRING")
            ?? configuration[$"{MessagingOptions.SectionName}:ConnectionString"]
            ?? configuredValue;

        return NormalizeConnectionString(connectionString)
            ?? NormalizeConnectionString(Environment.GetEnvironmentVariable("DATABASE_URL"))
            ?? string.Empty;
    }

    private static string? NormalizeConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        if (!Uri.TryCreate(connectionString, UriKind.Absolute, out var uri)) return connectionString;
        if (uri.Scheme is not "postgres" and not "postgresql") return connectionString;

        var userInfo = uri.UserInfo.Split(':', 2, StringSplitOptions.TrimEntries);
        var builder = new StringBuilder();

        AppendSetting(builder, "Host", uri.Host);
        AppendSetting(builder, "Port", uri.IsDefaultPort ? "5432" : uri.Port.ToString());
        AppendSetting(builder, "Database", uri.AbsolutePath.Trim('/'));
        AppendSetting(builder, "Username", userInfo.ElementAtOrDefault(0) is { Length: > 0 } username ? Uri.UnescapeDataString(username) : null);
        AppendSetting(builder, "Password", userInfo.ElementAtOrDefault(1) is { Length: > 0 } password ? Uri.UnescapeDataString(password) : null);

        foreach (var queryParameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = queryParameter.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length is 0 || string.IsNullOrWhiteSpace(parts[0])) continue;

            AppendSetting(builder, MapConnectionStringKey(parts[0]), parts.Length is 2 ? Uri.UnescapeDataString(parts[1]) : null);
        }

        return builder.ToString();
    }

    private static string MapConnectionStringKey(string key) => key.ToLowerInvariant() switch
    {
        "sslmode" => "SSL Mode",
        "trustservercertificate" => "Trust Server Certificate",
        "trust_server_certificate" => "Trust Server Certificate",
        "pooling" => "Pooling",
        _ => key
    };

    private static void AppendSetting(StringBuilder builder, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (builder.Length > 0) builder.Append(';');
        builder.Append(key).Append('=').Append(value);
    }
}
