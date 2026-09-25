using System.Diagnostics;
using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using PersonalAgent.Api.Services;
using PersonalAgent.Contracts.Automations;
using PersonalAgent.Integrations;

namespace PersonalAgent.Api.Automations;

internal sealed class AutomationStepConsumer(AutomationDbContext Db, AutomationAuthorization Authorization, AutomationRecipes Recipes,
    IAgentToolRegistry Registry, ToolAccessService Permissions, IServiceProvider Services, ILogger<AutomationStepConsumer> Logger) : IConsumer<ExecuteAutomationStep>
{
    public async Task Consume(ConsumeContext<ExecuteAutomationStep> Context)
    {
        var Message = Context.Message;
        using var Span = AutomationTelemetry.Start("automation.execute_step", Message.RunId, Message.StepIndex);
        var Start = Stopwatch.GetTimestamp();
        var Action = "unknown";
        var Status = "Failed";
        var Endpoint = await Context.GetSendEndpoint(AutomationRegistration.SagaAddress);
        try
        {
            using var Deadline = CancellationTokenSource.CreateLinkedTokenSource(Context.CancellationToken);
            Deadline.CancelAfter(TimeSpan.FromSeconds(60));
            var Token = Deadline.Token;
            var Run = await Db.Runs.AsNoTracking().SingleOrDefaultAsync(R => R.CorrelationId == Message.RunId, Token);
            if (Run is null || Run.CurrentState != "Running" || Run.StepIndex != Message.StepIndex) return;
            var Definition = await Db.Automations.AsNoTracking().SingleAsync(D => D.Id == Run.AutomationId, Token);
            var Access = await Authorization.ForRunAsync(Definition, Token);
            var Version = await Db.Versions.AsNoTracking().SingleAsync(V => V.AutomationId == Run.AutomationId && V.Version == Run.Version, Token);
            var Recipe = Recipes.Parse(Version.Source);
            var Step = Recipe.Steps[Message.StepIndex];
            Action = Step.Action;
            Span?.SetTag("automation.action", Action);
            await Recipes.ValidateToolsAsync(new([Step]), Services, Access, Permissions, Token);
            var Earlier = await Db.Steps.AsNoTracking().Where(S => S.RunId == Run.CorrelationId && S.Index < Run.StepIndex).ToListAsync(Token);
            var Skip = Step.When is { } Condition && Earlier.Single(S => S.StepId == Condition.Step).Output != Condition.Expected;
            var Arguments = AutomationRecipes.Resolve(Step, Run, Earlier);
            var Output = "";
            if (!Skip)
            {
                if (Action == "csharp")
                {
                    var Input = Arguments.GetProperty("input").GetString()!;
                    if (Input.Length > AutomationPrograms.MaxInput) throw new ArgumentException("Program input exceeds 64 KiB.");
                    if (AutomationRecipes.Packages(Arguments).Length > 0)
                        await Services.GetRequiredService<AutomationOperationGateway>().ApprovePackagesAsync(Run.CorrelationId, Run.StepIndex, Step, Token);
                    await (await Context.GetSendEndpoint(new Uri("queue:" + AutomationPrograms.Queue))).Send(
                        new ExecuteAutomationProgram(Run.CorrelationId, Run.StepIndex, Arguments.GetProperty("source").GetString()!, Input, DateTimeOffset.UtcNow.AddMinutes(3),
                            AutomationRecipes.Packages(Arguments), Arguments.TryGetProperty("packageLock", out var Lock) ? Lock.GetString() : null), Token);
                    Status = "Dispatched";
                    return; // The isolated runner reports completion; dispatch is not successful execution.
                }
                if (Action == "tool")
                {
                    var Key = Arguments.GetProperty("tool").GetString()!;
                    var Function = Registry.GetRegistrations().Single(T => T.Descriptor.Key == Key).CreateFunction(Services, Access);
                    var Inputs = Arguments.GetProperty("inputs").EnumerateObject().ToDictionary(P => P.Name, P => (object?)P.Value.Clone());
                    var Result = await Function.InvokeAsync(new AIFunctionArguments(Inputs), Token);
                    Output = Result is string Text ? Text : JsonSerializer.Serialize(Result);
                }
                else Output = Action == "text" ? Arguments.GetProperty("text").GetString()! : Arguments.GetRawText();
                if (Output.Length > 65536) throw new ArgumentException("Step output exceeds 64 KiB.");
                if (Action is "notify" or "save_report")
                {
                    var Title = Arguments.GetProperty("title").GetString();
                    var Body = Arguments.GetProperty(Action == "notify" ? "body" : "content").GetString();
                    if (string.IsNullOrWhiteSpace(Title) || Title.Length > 160 || string.IsNullOrWhiteSpace(Body)
                        || Body.Length > (Action == "notify" ? 4000 : 60000)) throw new ArgumentException("Invalid title or content size.");
                }
            }
            await Endpoint.Send(new AutomationStepCompleted(Run.CorrelationId, Run.StepIndex, Output, Skip), Context.CancellationToken);
            Status = Skip ? "Skipped" : "Completed";
            Logger.LogInformation("Automation step {RunId}/{StepIndex} action {Action} produced {Status}", Message.RunId, Message.StepIndex, Action, Status);
        }
        catch (OperationCanceledException) when (Context.CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception Exception)
        {
            var Blocked = Exception is UnauthorizedAccessException;
            var Error = Blocked ? "An action is no longer authorized." : Exception is OperationCanceledException
                ? "Step exceeded its 60-second deadline." : "Step execution failed. Inspect correlated logs for the exception type.";
            Span?.SetStatus(ActivityStatusCode.Error);
            Span?.SetTag("error.type", Exception.GetType().FullName);
            Logger.LogError(new EventId(4302, "AutomationStepFailed"), Exception, "Automation step {RunId}/{StepIndex} failed with {ExceptionType}", Message.RunId, Message.StepIndex, Exception.GetType().Name);
            await Endpoint.Send(new AutomationStepFailed(Message.RunId, Message.StepIndex, Error, Blocked), Context.CancellationToken);
        }
        finally { AutomationTelemetry.StepFinished(Action, Status, Stopwatch.GetElapsedTime(Start).TotalSeconds); }
    }
}
