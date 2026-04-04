using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using PersonalAgent.Configuration;

namespace PersonalAgent.Services;

internal sealed class TavilyMcpToolProvider : ITavilyMcpToolProvider, IHostedService, IAsyncDisposable
{
    private readonly ApiKeyOptions _options;
    private readonly ILogger<TavilyMcpToolProvider> _logger;
    private IReadOnlyList<AIFunction> _tools = [];
    private McpClient? _mcpClient;
    public bool IsAvailable => _tools.Count > 0;
    public string Status { get; private set; } = "NotStarted";

    public TavilyMcpToolProvider(IOptions<ApiKeyOptions> options, ILogger<TavilyMcpToolProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public IReadOnlyList<AIFunction> GetTools() => _tools;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Initializing Tavily MCP provider; enabled={Enabled}; endpoint={Endpoint}; hasDefaultParameters={HasDefaultParameters}",
            _options.EnableWebSearch,
            _options.TavilyMcpUrl,
            !string.IsNullOrWhiteSpace(_options.TavilyDefaultParameters));

        if (!_options.EnableWebSearch)
        {
            Status = "Disabled";
            _logger.LogInformation("Tavily MCP web search is disabled");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.TavilyApiKey))
        {
            Status = "MissingApiKey";
            _logger.LogInformation("Tavily MCP web search is not configured because API key is missing");
            return;
        }

        try
        {
            var endpoint = new Uri(_options.TavilyMcpUrl, UriKind.Absolute);
            var headers = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {_options.TavilyApiKey}"
            };

            if (!string.IsNullOrWhiteSpace(_options.TavilyDefaultParameters))
                headers["DEFAULT_PARAMETERS"] = _options.TavilyDefaultParameters;

            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = "TavilyRemoteMcp",
                Endpoint = endpoint,
                AdditionalHeaders = headers
            });

            _mcpClient = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
            var discoveredTools = await _mcpClient.ListToolsAsync(cancellationToken: cancellationToken);
            _tools = [.. discoveredTools.Cast<AIFunction>()];
            Status = _tools.Count > 0 ? "Ready" : "ConnectedNoTools";

            _logger.LogInformation(
                "Connected Tavily MCP with {ToolCount} tools; toolNames={ToolNames}",
                _tools.Count,
                _tools.Count > 0 ? string.Join(",", _tools.Select(tool => tool.Name)) : "none");
        }
        catch (Exception ex)
        {
            _tools = [];
            Status = "InitializationFailed";
            _logger.LogWarning(ex, "Failed to initialize Tavily MCP; web tools will be unavailable");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Tavily MCP provider; status={Status}; availableTools={ToolCount}", Status, _tools.Count);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("Disposing Tavily MCP provider; status={Status}; availableTools={ToolCount}", Status, _tools.Count);
        if (_mcpClient is not null)
            await _mcpClient.DisposeAsync();
    }
}
