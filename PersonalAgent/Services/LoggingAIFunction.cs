using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace PersonalAgent.Services;

internal sealed class LoggingAIFunction(AIFunction innerFunction, ILogger logger, string source) : AIFunction
{
    public override string Name => innerFunction.Name;
    public override string Description => innerFunction.Description;
    public override JsonElement JsonSchema => innerFunction.JsonSchema;
    public override JsonElement? ReturnJsonSchema => innerFunction.ReturnJsonSchema;
    public override JsonSerializerOptions JsonSerializerOptions => innerFunction.JsonSerializerOptions;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("Invoking tool {ToolName} from {ToolSource}", Name, source);

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
