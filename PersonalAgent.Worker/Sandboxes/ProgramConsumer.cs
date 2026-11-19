using System.Diagnostics;
using MassTransit;
using PersonalAgent.Contracts.Automations;
using PersonalAgent.Integrations;

namespace PersonalAgent.Worker.Sandboxes;

internal class ProgramConsumer(IProgramExecutor Executor, ILogger<ProgramConsumer> Logger) : IConsumer<ExecuteAutomationProgram>
{
    public async Task Consume(ConsumeContext<ExecuteAutomationProgram> Context)
    {
        var Message = Context.Message;
        var Endpoint = await Context.GetSendEndpoint(new Uri("queue:personal-agent-automation-runs"));
        if (Message.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await Endpoint.Send(new AutomationStepFailed(Message.RunId, Message.StepIndex, "Sandbox dispatch expired before execution. Check runner availability."), Context.CancellationToken);
            return;
        }
        using var Span = AutomationTelemetry.Start("automation." + Message.Language, Message.RunId, Message.StepIndex);
        Span?.SetTag("automation.action", Message.Language);
        var Result = await Executor.ExecuteAsync(Message, Context.CancellationToken);
        if (Result.Evidence.Status == "Completed")
            await Endpoint.Send(new AutomationStepCompleted(Message.RunId, Message.StepIndex, Result.Output, Program: Result.Evidence), Context.CancellationToken);
        else
        {
            Span?.SetStatus(ActivityStatusCode.Error);
            Logger.LogError(new EventId(4304, "AutomationProgramFailed"), "Program step {RunId}/{StepIndex} ended with {Status}", Message.RunId, Message.StepIndex, Result.Evidence.Status);
            await Endpoint.Send(new AutomationStepFailed(Message.RunId, Message.StepIndex,
                $"Sandbox program {Result.Evidence.Status}. Inspect program diagnostics.", Program: Result.Evidence), Context.CancellationToken);
        }
        AutomationTelemetry.StepFinished(Message.Language, Result.Evidence.Status, Result.Evidence.DurationSeconds);
        Logger.LogInformation("Program step {RunId}/{StepIndex} finished with {Status} using image {ImageId}",
            Message.RunId, Message.StepIndex, Result.Evidence.Status, Result.Evidence.ImageId);
    }
}

internal sealed class ScratchpadConsumer(IProgramExecutor Executor, ILogger<ProgramConsumer> Logger) : ProgramConsumer(Executor, Logger);

internal sealed class SandboxStartup(DockerProgramExecutor Executor) : IHostedService
{
    public Task StartAsync(CancellationToken Token) => Executor.InitializeAsync(Token);
    public Task StopAsync(CancellationToken Token) => Task.CompletedTask;
}
