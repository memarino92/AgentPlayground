using System.Runtime.CompilerServices;

using Microsoft.Extensions.AI;
using System.ClientModel;
using System.ClientModel.Primitives;
using OpenAI;
using OpenAI.Responses;
using PersonalAgent.Integrations.ChatGpt;

namespace PersonalAgent.Api.Services;

// The pinned OpenAI SDK marks Responses as experimental. Keep the opt-in confined to this adapter.
#pragma warning disable OPENAI001

internal sealed class ChatGptChatClient(ChatGptConnectionStore Store, string ActorId, Guid ConnectionId, string ModelId,
    Func<string, IChatClient>? ClientFactory = null) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> Messages, ChatOptions? Options = null, CancellationToken Token = default)
        => await GetStreamingResponseAsync(Messages, Options, Token).ToChatResponseAsync(cancellationToken: Token);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> Messages,
        ChatOptions? Options = null, [EnumeratorCancellation] CancellationToken Token = default)
    {
        var credentials = await Store.GetCredentialsAsync(ActorId, ConnectionId, Token);
        if (!ChatGptOAuth.PlanEnabled(credentials.Scope)) throw new ChatGptException("ChatGPT plan usage was not authorized. Reconnect in Settings.");
        using var client = ClientFactory is not null ? ClientFactory(credentials.AccessToken)
            : new ResponsesClient(new ApiKeyCredential(credentials.AccessToken), new OpenAIClientOptions
            {
                RetryPolicy = new ClientRetryPolicy(0), NetworkTimeout = TimeSpan.FromMinutes(2)
            }).AsIChatClient(ModelId);
        var options = Options?.Clone() ?? new ChatOptions();
        options.ModelId = ModelId;
        options.ConversationId = null; // store:false requires replaying context rather than referencing a stored response.
        options.RawRepresentationFactory = _ => new CreateResponseOptions { StoredOutputEnabled = false };
        var completed = false;
        await using var stream = client.GetStreamingResponseAsync(Messages, options, Token).GetAsyncEnumerator(Token);
        while (await MoveNextAsync(stream))
        {
            var update = stream.Current;
            if (update.RawRepresentation is StreamingResponseFailedUpdate)
                throw new ChatGptException("ChatGPT could not complete this request. Review your plan or this app’s limit in ChatGPT Settings → Usage.");
            if (update.RawRepresentation is StreamingResponseIncompleteUpdate)
                throw new ChatGptException("The ChatGPT response was incomplete. Try again with a smaller request.");
            if (update.RawRepresentation is StreamingResponseCompletedUpdate) completed = true;
            yield return update;
        }
        if (!completed) throw new ChatGptException("ChatGPT did not complete the response. Review usage in ChatGPT Settings and retry.");
    }

    private static async Task<bool> MoveNextAsync(IAsyncEnumerator<ChatResponseUpdate> Stream)
    {
        try { return await Stream.MoveNextAsync(); }
        catch (ClientResultException Exception) when (Exception.Status is 401 or 403)
        {
            throw new ChatGptException("ChatGPT access is no longer available. Reconnect this account in Settings.");
        }
        catch (ClientResultException Exception) when (Exception.Status == 429)
        {
            throw new ChatGptException("ChatGPT usage limit reached. Review your plan or this app’s limit in ChatGPT Settings → Usage.");
        }
    }

    public object? GetService(Type ServiceType, object? ServiceKey = null) => ServiceType == typeof(IChatClient) && ServiceKey is null ? this : null;
    public void Dispose() { }
}
#pragma warning restore OPENAI001
