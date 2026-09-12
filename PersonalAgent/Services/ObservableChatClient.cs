using System.Diagnostics;
using System.Runtime.CompilerServices;
using AgentPlayground.Integrations;
using Microsoft.Extensions.AI;

namespace PersonalAgent.Services;

internal sealed class ObservableChatClient(IChatClient Inner, string Model) : DelegatingChatClient(Inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> Messages, ChatOptions? Options = null,
        CancellationToken CancellationToken = default) => AiTelemetry.RunAsync("chat", "LLM", async () =>
        {
            var response = await base.GetResponseAsync(Messages, Options, CancellationToken);
            AiTelemetry.SetUsage(Activity.Current, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount, response.Usage?.TotalTokenCount);
            return response;
        }, Model);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> Messages,
        ChatOptions? Options = null, [EnumeratorCancellation] CancellationToken CancellationToken = default)
    {
        using var activity = AiTelemetry.Start("chat", "LLM", Model);
        await using var updates = base.GetStreamingResponseAsync(Messages, Options, CancellationToken).GetAsyncEnumerator(CancellationToken);
        while (true)
        {
            bool next;
            try { next = await updates.MoveNextAsync(); }
            catch (OperationCanceledException)
            {
                activity?.SetTag("error.type", "cancelled");
                throw;
            }
            catch (Exception exception)
            {
                activity?.SetTag("error.type", exception.GetType().FullName);
                activity?.SetStatus(ActivityStatusCode.Error);
                throw;
            }
            if (!next) yield break;
            foreach (var usage in updates.Current.Contents.OfType<UsageContent>())
                AiTelemetry.SetUsage(activity, usage.Details.InputTokenCount, usage.Details.OutputTokenCount, usage.Details.TotalTokenCount);
            yield return updates.Current;
        }
    }
}
