using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentPlayground.Contracts.Configuration;

/// <summary>Preserves IOptions consumers while resolving the current validated monitor value.</summary>
public sealed class LiveOptions<T>(IOptionsMonitor<T> Monitor) : IOptions<T> where T : class
{
    public T Value => Monitor.CurrentValue;
}

public static class LiveOptionsExtensions
{
    public static IServiceCollection AddLiveOptions<T>(this IServiceCollection Services, IConfiguration Configuration) where T : class
    {
        Services.AddSingleton<IOptionsChangeTokenSource<T>>(new ConfigurationChangeTokenSource<T>(Configuration));
        Services.AddSingleton<IOptions<T>, LiveOptions<T>>();
        return Services;
    }
}
