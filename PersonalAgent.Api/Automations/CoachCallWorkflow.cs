using MassTransit;
using PersonalAgent.Contracts.Messaging;
using PersonalAgent.Contracts.Messaging.Commands;

namespace PersonalAgent.Api.Automations;

internal sealed class CoachCallWorkflow : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }
    public Guid SessionId { get; set; }
    public Guid ProviderCorrelationId { get; set; }
    public string ProfileId { get; set; } = "";
    public string CurrentState { get; set; } = "Initial";
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class CoachCallWorkflowStateMachine : MassTransitStateMachine<CoachCallWorkflow>
{
    public State Transcribing { get; private set; } = null!;
    public State AwaitingSpeakerOverride { get; private set; } = null!;
    public State Processing { get; private set; } = null!;
    public State Summarizing { get; private set; } = null!;
    public State Completed { get; private set; } = null!;
    public State Failed { get; private set; } = null!;
    public Event<StartCoachCallWorkflow> Start { get; private set; } = null!;
    public Event<CoachCallWorkflowSignal> Signal { get; private set; } = null!;

    public CoachCallWorkflowStateMachine()
    {
        InstanceState(S => S.CurrentState);
        Event(() => Start, E => E.CorrelateById(C => C.Message.UploadId));
        Event(() => Signal, E => { E.CorrelateById(C => C.Message.UploadId); E.OnMissingInstance(M => M.Discard()); });
        Initially(When(Start)
            .Then(C =>
            {
                C.Saga.SessionId = C.Message.SessionId;
                C.Saga.ProfileId = C.Message.ProfileId;
                C.Saga.ProviderCorrelationId = C.Message.CorrelationId;
                C.Saga.UpdatedAt = DateTimeOffset.UtcNow;
            })
            .IfElse(C => C.Message.Status == "AwaitingSpeakerOverride",
                B => B.TransitionTo(AwaitingSpeakerOverride),
                B => B.IfElse(C => C.Message.Status == "Processing",
                    D => D.ThenAsync(async C => await SendAsync(C, MessagingEndpointNames.CoachCallProcessing,
                        new ProcessCoachTranscriptCommand(C.Saga.CorrelationId, C.Saga.SessionId, C.Saga.ProfileId, C.Saga.ProviderCorrelationId)))
                        .TransitionTo(Processing),
                    D => D.ThenAsync(async C => await SendAsync(C, MessagingEndpointNames.CoachCallTranscription,
                        new TranscribeCoachCallCommand(C.Saga.CorrelationId, C.Saga.ProfileId, C.Saga.ProviderCorrelationId)))
                        .TransitionTo(Transcribing))));
        During(Transcribing,
            Ignore(Start),
            When(Signal, C => Matches(C) && C.Message.Stage == "AwaitingSpeakerOverride")
                .Then(Touch).TransitionTo(AwaitingSpeakerOverride),
            When(Signal, C => Matches(C) && C.Message.Stage == "Processing")
                .ThenAsync(DispatchProcessingAsync).TransitionTo(Processing),
            When(Signal, C => Matches(C) && C.Message.Stage == "Failed")
                .Then(Touch).TransitionTo(Failed));
        During(AwaitingSpeakerOverride,
            Ignore(Start),
            When(Signal, C => Matches(C) && C.Message.Stage == "AwaitingSpeakerOverride").Then(Touch),
            When(Signal, C => Matches(C) && C.Message.Stage == "Processing")
                .ThenAsync(DispatchProcessingAsync).TransitionTo(Processing),
            When(Signal, C => Matches(C) && C.Message.Stage == "Failed")
                .Then(Touch).TransitionTo(Failed));
        During(Processing,
            Ignore(Start),
            When(Signal, C => Matches(C) && C.Message.Stage == "Processing").Then(Touch),
            When(Signal, C => Matches(C) && C.Message.Stage == "Completed")
                .ThenAsync(async C =>
                {
                    Touch(C);
                    await SendAsync(C, "personal-agent-coach-call-summaries",
                        new GenerateCoachCallSummary(C.Saga.CorrelationId, C.Saga.SessionId, C.Saga.ProfileId));
                }).TransitionTo(Summarizing),
            When(Signal, C => Matches(C) && C.Message.Stage == "Failed")
                .Then(Touch).TransitionTo(Failed));
        During(Summarizing,
            Ignore(Start),
            When(Signal, C => Matches(C) && C.Message.Stage == "Completed").Then(Touch),
            When(Signal, C => Matches(C) && C.Message.Stage == "Summarized")
                .Then(Touch).TransitionTo(Completed));
        During(Completed, Ignore(Start), Ignore(Signal));
        During(Failed, Ignore(Start), Ignore(Signal));
    }

    private static bool Matches(BehaviorContext<CoachCallWorkflow, CoachCallWorkflowSignal> C) =>
        C.Message.ProfileId == C.Saga.ProfileId && C.Message.SessionId == C.Saga.SessionId;
    private static void Touch(BehaviorContext<CoachCallWorkflow, CoachCallWorkflowSignal> C) => C.Saga.UpdatedAt = DateTimeOffset.UtcNow;
    private static async Task DispatchProcessingAsync(BehaviorContext<CoachCallWorkflow, CoachCallWorkflowSignal> C)
    {
        Touch(C);
        await SendAsync(C, MessagingEndpointNames.CoachCallProcessing,
            new ProcessCoachTranscriptCommand(C.Saga.CorrelationId, C.Saga.SessionId, C.Saga.ProfileId, C.Saga.ProviderCorrelationId));
    }
    private static async Task SendAsync<T>(BehaviorContext<CoachCallWorkflow, T> C, string Queue, object Message) where T : class =>
        await (await C.GetSendEndpoint(new Uri($"queue:{Queue}"))).Send(Message, Message.GetType(), C.CancellationToken);
}

internal sealed class CoachCallWorkflowDefinition : SagaDefinition<CoachCallWorkflow>
{
    public CoachCallWorkflowDefinition() { EndpointName = "personal-agent-coach-call-workflows"; ConcurrentMessageLimit = 8; }
    protected override void ConfigureSaga(IReceiveEndpointConfigurator Endpoint, ISagaConfigurator<CoachCallWorkflow> Saga, IRegistrationContext Context)
    {
        Endpoint.UseMessageRetry(R => R.Intervals(100, 500, 1000));
        Endpoint.UseEntityFrameworkOutbox<AutomationDbContext>(Context);
    }
}
