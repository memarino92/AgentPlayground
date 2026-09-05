using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal sealed class LoggingAIFunction(
    AIFunction innerFunction,
    ILogger logger,
    string source,
    string toolKey,
    AgentAccessContext access,
    ToolAccessService toolAccessService) : AIFunction
{
    public override string Name => innerFunction.Name;
    public override string Description => innerFunction.Description;
    public override JsonElement JsonSchema => innerFunction.JsonSchema;
    public override JsonElement? ReturnJsonSchema => innerFunction.ReturnJsonSchema;
    public override JsonSerializerOptions JsonSerializerOptions => innerFunction.JsonSerializerOptions;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!await toolAccessService.IsAllowedAsync(access.Role, toolKey, cancellationToken))
        {
            logger.LogWarning(
                "Denied tool {ToolName} for actor {ActorId}, role {Role}, subject {SubjectProfileId}",
                Name,
                access.ActorId,
                access.Role,
                access.SubjectProfileId);
            throw new UnauthorizedAccessException($"Role '{access.Role}' is not allowed to invoke tool '{Name}'.");
        }

        logger.LogInformation(
            "Invoking tool {ToolName} from {ToolSource} for actor {ActorId}, role {Role}, subject {SubjectProfileId}",
            Name,
            source,
            access.ActorId,
            access.Role,
            access.SubjectProfileId);

        try
        {
            var result = await innerFunction.InvokeAsync(arguments, cancellationToken);
            logger.LogInformation(
                "Tool {ToolName} from {ToolSource} completed in {ElapsedMilliseconds}ms",
                Name,
                source,
                stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Tool {ToolName} from {ToolSource} failed after {ElapsedMilliseconds}ms",
                Name,
                source,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
