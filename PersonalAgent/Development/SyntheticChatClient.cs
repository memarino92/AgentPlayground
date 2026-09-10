using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

using PersonalAgent.Services;

namespace PersonalAgent.Development;

internal sealed class SyntheticChatClient : IChatClient, IAgentChatClientFactory
{
    public IChatClient Create(string ModelId) => this;
    public object? GetService(Type ServiceType, object? ServiceKey = null) => ServiceKey is null && ServiceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> Messages, ChatOptions? Options = null, CancellationToken CancellationToken = default)
    {
        CancellationToken.ThrowIfCancellationRequested();
        var Memory = Messages.LastOrDefault(Message => Message.Role == ChatRole.System && Message.Text.StartsWith("Use these remembered", StringComparison.Ordinal))?.Text;
        var Text = Memory is null
            ? "Synthetic demo response. Try: remember that my favorite exercise is deadlift, then ask about my favorite exercise. Upload a small .m4a file to see the fixed two-speaker review example."
            : "Synthetic demo recall from this account's stored conversation:\n" + Memory[(Memory.IndexOf('\n') + 1)..];
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Text)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> Messages, ChatOptions? Options = null, [EnumeratorCancellation] CancellationToken CancellationToken = default)
    {
        var Response = await GetResponseAsync(Messages, Options, CancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, Response.Text);
    }
}
