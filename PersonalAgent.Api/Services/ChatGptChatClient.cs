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
        options.ContinuationToken = null;
        options.AllowBackgroundResponses = null;
        options.Temperature = options.TopP = null;
        options.MaxOutputTokens = null;
        options.AdditionalProperties = null;
        var rawOptions = new CreateResponseOptions { StoredOutputEnabled = false };
        if (options.Tools is { Count: > 0 } tools)
        {
            if (tools.Any(Tool => Tool is not AIFunctionDeclaration))
                throw new ChatGptException("This ChatGPT connection supports locally executed function tools only.");
            var grouped = BinaryData.FromObjectAsJson(new
            {
                type = "namespace", name = "personal_agent", description = "Functions authorized by Personal Agent for the current account.",
                tools = tools.Cast<AIFunctionDeclaration>().Select(Function => new
                {
                    type = "function", name = Function.Name, description = Function.Description,
                    parameters = Function.JsonSchema, strict = false
                }).ToArray()
            });
            rawOptions.Tools.Add(ModelReaderWriter.Read<ResponseTool>(grouped, ModelReaderWriterOptions.Json, OpenAIContext.Default)!);
            rawOptions.ParallelToolCallsEnabled = options.AllowMultipleToolCalls;
            rawOptions.ToolChoice = options.ToolMode switch
            {
                NoneChatToolMode => ResponseToolChoice.CreateNoneChoice(),
                RequiredChatToolMode { RequiredFunctionName: null } => ResponseToolChoice.CreateRequiredChoice(),
                RequiredChatToolMode Required => ModelReaderWriter.Read<ResponseToolChoice>(BinaryData.FromObjectAsJson(
                    new { type = "function", name = Required.RequiredFunctionName, @namespace = "personal_agent" }), ModelReaderWriterOptions.Json, OpenAIContext.Default),
                _ => ResponseToolChoice.CreateAutoChoice()
            };
            options.Tools = null; // Already grouped; the SDK must not also emit bare top-level function tools.
        }
        options.RawRepresentationFactory = _ => rawOptions;
        var messages = Messages.Select(Message =>
        {
            if (Message.Role != ChatRole.System) return Message;
            var clone = Message.Clone();
            clone.Role = new ChatRole("developer");
            clone.RawRepresentation = null;
            return clone;
        }).ToArray();
        var completed = false;
        await using var stream = client.GetStreamingResponseAsync(messages, options, Token).GetAsyncEnumerator(Token);
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
        catch (ClientResultException Exception) when (Exception.Status == 400)
        {
            throw new ChatGptException("ChatGPT rejected this request. Please retry; if it persists, the provider integration needs attention.");
        }
    }

    public object? GetService(Type ServiceType, object? ServiceKey = null) => ServiceType == typeof(IChatClient) && ServiceKey is null ? this : null;
    public void Dispose() { }
}
#pragma warning restore OPENAI001
