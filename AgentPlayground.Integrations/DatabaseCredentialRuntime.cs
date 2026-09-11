using AgentPlayground.Contracts.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AgentPlayground.Integrations;

public sealed record DatabaseCredentialStatus(string Status, DateTimeOffset? LastChecked, string[] Overrides);

public sealed class DatabaseCredentialRuntime(IConfiguration Configuration)
{
    private DatabaseCredentialStatus Current = new("Not checked", null, []);
    public DatabaseCredentialStatus Status => Volatile.Read(ref Current);

    public async Task<DatabaseCredentialStatus> ReloadAsync(CancellationToken CancellationToken)
    {
        var providers = (Configuration as IConfigurationRoot)?.Providers.OfType<PostgresConfigurationProvider>().ToArray() ?? [];
        var status = "No database configuration provider; deployment bootstrap is required";
        try
        {
            foreach (var provider in providers) await provider.ReloadCredentialsAsync(CancellationToken);
            if (providers.Length > 0) status = "API provider credentials checked; new requests use the accepted values";
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested) { throw; }
        catch { status = "Credential reload failed; check saved values and database connectivity"; }
        var result = new DatabaseCredentialStatus(status, DateTimeOffset.UtcNow,
            LiveCredentialPolicy.EnvironmentVariables.Values.Where(Name => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Name))).ToArray());
        Volatile.Write(ref Current, result);
        return result;
    }
}

public sealed class DatabaseCredentialReloadWorker(DatabaseCredentialRuntime Runtime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken StoppingToken)
    {
        while (!StoppingToken.IsCancellationRequested)
        {
            await Runtime.ReloadAsync(StoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(15), StoppingToken);
        }
    }
}
