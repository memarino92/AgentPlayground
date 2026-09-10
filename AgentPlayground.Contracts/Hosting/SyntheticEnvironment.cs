using AgentPlayground.Contracts.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace AgentPlayground.Contracts.Hosting;

public static class SyntheticEnvironment
{
    public static bool IsEnabled(IConfiguration Configuration, IHostEnvironment Environment)
    {
        if (!Configuration.GetValue<bool>("SyntheticDemo:Enabled")) return false;
        if (!Environment.IsDevelopment()) throw new InvalidOperationException("SyntheticDemo requires the Development environment.");
        ValidateTarget(System.Environment.GetEnvironmentVariable("DATABASE_URL") ?? "");
        foreach (var Name in new[] { "MESSAGING_CONNECTION_STRING", "AGENT_MEMORY_CONNECTION_STRING" })
            if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(Name)))
                throw new InvalidOperationException($"Remove {Name} before starting the synthetic demo.");
        return true;
    }

    public static string ValidateTarget(string ConnectionString)
    {
        var Normalized = PostgresConnectionStringNormalizer.Normalize(ConnectionString);
        if (string.IsNullOrWhiteSpace(Normalized)) throw new InvalidOperationException("Synthetic demo requires DATABASE_URL.");
        var Target = new NpgsqlConnectionStringBuilder(Normalized);
        if (Target.Database != "agentplayground_demo" || Target.Host is not ("localhost" or "127.0.0.1" or "::1" or "postgres-demo"))
            throw new InvalidOperationException("Synthetic demo requires the dedicated local agentplayground_demo database.");
        return Normalized;
    }

    public static async Task<bool> RunHealthProbeAsync(string[] Args)
    {
        if (Args is not ["--healthcheck"]) return false;
        using var Client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var Response = await Client.GetAsync($"http://localhost:{System.Environment.GetEnvironmentVariable("PORT") ?? "8080"}/health/ready");
            System.Environment.ExitCode = Response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException) { System.Environment.ExitCode = 1; }
        catch (TaskCanceledException) { System.Environment.ExitCode = 1; }
        return true;
    }
}
