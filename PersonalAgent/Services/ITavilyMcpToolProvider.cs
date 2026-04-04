using Microsoft.Extensions.AI;

namespace PersonalAgent.Services;

internal interface ITavilyMcpToolProvider
{
    IReadOnlyList<AIFunction> GetTools();
    bool IsAvailable { get; }
    string Status { get; }
}
