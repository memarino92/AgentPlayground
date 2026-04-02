using Microsoft.Extensions.Configuration;

namespace AgentPlayground.Contracts.Configuration;

public static class ConfigurationValueResolver
{
    public static string? ResolveString(IConfiguration configuration, string envVarName, string configKey, string? fallback = null)
    {
        var envValue = Environment.GetEnvironmentVariable(envVarName);
        if (!string.IsNullOrWhiteSpace(envValue)) return envValue;

        var configValue = configuration[configKey];
        if (!string.IsNullOrWhiteSpace(configValue)) return configValue;

        return fallback;
    }

    public static bool ResolveBool(IConfiguration configuration, string envVarName, string configKey, bool fallback)
    {
        var candidate = ResolveString(configuration, envVarName, configKey);
        return bool.TryParse(candidate, out var parsed) ? parsed : fallback;
    }

    public static int ResolveInt(IConfiguration configuration, string envVarName, string configKey, int fallback, Func<int, bool>? predicate = null)
    {
        var candidate = ResolveString(configuration, envVarName, configKey);
        if (!int.TryParse(candidate, out var parsed)) return fallback;
        if (predicate is not null && !predicate(parsed)) return fallback;
        return parsed;
    }
}
