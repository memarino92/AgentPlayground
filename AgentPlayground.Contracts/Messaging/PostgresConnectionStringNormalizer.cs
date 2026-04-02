using System.Text;

namespace AgentPlayground.Contracts.Messaging;

public static class PostgresConnectionStringNormalizer
{
    public static string? Normalize(string? connectionString)
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
