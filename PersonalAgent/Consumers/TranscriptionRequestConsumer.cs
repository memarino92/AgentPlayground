using AgentPlayground.Contracts.Messaging.Requests;
using MassTransit;
using PersonalAgent.Services;

namespace PersonalAgent.Consumers;

internal class TranscriptionRequestConsumer(TranscriptionJobService Jobs) : IConsumer<TranscriptionRequest>
{
    public async Task Consume(ConsumeContext<TranscriptionRequest> Context) =>
        await Context.RespondAsync(await Jobs.GetAsync(Context.Message.UploadId, Context.Message.ProfileId, Context.CancellationToken));
}
