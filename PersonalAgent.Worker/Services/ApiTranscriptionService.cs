using AgentPlayground.Contracts.Messaging.Requests;
using AgentPlayground.Contracts.Messaging.Responses;
using MassTransit;
using PersonalAgent.Worker.Models;

namespace PersonalAgent.Worker.Services;

internal class ApiTranscriptionService(IRequestClient<TranscriptionRequest> Client) : ITranscriptionService
{
    public async Task<List<TranscribedUtterance>> TranscribeAsync(Guid UploadId, string ProfileId, CancellationToken CancellationToken = default)
    {
        while (true)
        {
            var Response = (await Client.GetResponse<TranscriptionResponse>(new(UploadId, ProfileId), CancellationToken)).Message;
            if (Response.Status == TranscriptionStatus.Failed) throw new TranscriptionFailedException(Response.Error ?? "Transcription failed.");
            if (Response.Status == TranscriptionStatus.Completed)
                return Response.Segments.Select(Segment => new TranscribedUtterance(Segment.SpeakerLabel, "unknown", Segment.StartMs, Segment.EndMs, Segment.Text, Segment.Confidence)).ToList();
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(Response.RetryAfterSeconds, 1, 60)), CancellationToken);
        }
    }
}
