using PersonalAgent.Contracts.Messaging.Requests;
using MassTransit;
using PersonalAgent.Api.Services;

namespace PersonalAgent.Api.Consumers;

internal class TranscriptionRequestConsumer(TranscriptionJobService Jobs) : IConsumer<TranscriptionRequest>
{
    public async Task Consume(ConsumeContext<TranscriptionRequest> Context) =>
        await Context.RespondAsync(await Jobs.GetAsync(Context.Message.UploadId, Context.Message.ProfileId, Context.CancellationToken));
}
