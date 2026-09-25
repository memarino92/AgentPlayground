using System.Diagnostics;
using MassTransit;
using PersonalAgent.Contracts.Automations;
using PersonalAgent.Integrations;

namespace PersonalAgent.AutomationRunner;

internal sealed class ProgramConsumer(DockerProgramExecutor Executor, ILogger<ProgramConsumer> Logger) : IConsumer<ExecuteAutomationProgram>
{
    public async Task Consume(ConsumeContext<ExecuteAutomationProgram> Context)
    {
        var Message = Context.Message;
        var Endpoint = await Context.GetSendEndpoint(new Uri("queue:personal-agent-automation-runs"));
        if (Message.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await Endpoint.Send(new AutomationStepFailed(Message.RunId, Message.StepIndex, "C# dispatch expired before execution. Check runner availability."), Context.CancellationToken);
            return;
        }
        using var Span = AutomationTelemetry.Start("automation.csharp", Message.RunId, Message.StepIndex);
        Span?.SetTag("automation.action", "csharp");
        var Result = await Executor.ExecuteAsync(Message.Source, Message.Input, Context.CancellationToken);
        if (Result.Evidence.Status == "Completed")
            await Endpoint.Send(new AutomationStepCompleted(Message.RunId, Message.StepIndex, Result.Output, Program: Result.Evidence), Context.CancellationToken);
        else
        {
            Span?.SetStatus(ActivityStatusCode.Error);
            Logger.LogError(new EventId(4304, "AutomationProgramFailed"), "Program step {RunId}/{StepIndex} ended with {Status}", Message.RunId, Message.StepIndex, Result.Evidence.Status);
            await Endpoint.Send(new AutomationStepFailed(Message.RunId, Message.StepIndex,
                $"C# program {Result.Evidence.Status}. Inspect program diagnostics.", Program: Result.Evidence), Context.CancellationToken);
        }
        AutomationTelemetry.StepFinished("csharp", Result.Evidence.Status, Result.Evidence.DurationSeconds);
        Logger.LogInformation("Program step {RunId}/{StepIndex} finished with {Status} using image {ImageId}",
            Message.RunId, Message.StepIndex, Result.Evidence.Status, Result.Evidence.ImageId);
    }
}

internal sealed class SandboxStartup(DockerProgramExecutor Executor) : IHostedService
{
    public Task StartAsync(CancellationToken Token) => Executor.InitializeAsync(Token);
    public Task StopAsync(CancellationToken Token) => Task.CompletedTask;
}
