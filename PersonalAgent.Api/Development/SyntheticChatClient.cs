using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

using PersonalAgent.Api.Services;

namespace PersonalAgent.Api.Development;

internal sealed class SyntheticChatClient : IChatClient, IAgentChatClientFactory
{
    public IChatClient Create(string ModelId) => new ObservableChatClient(this, ModelId);
    public object? GetService(Type ServiceType, object? ServiceKey = null) => ServiceKey is null && ServiceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> Messages, ChatOptions? Options = null, CancellationToken CancellationToken = default)
    {
        CancellationToken.ThrowIfCancellationRequested();
        var Memory = Messages.LastOrDefault(Message => Message.Role == ChatRole.System && Message.Text.StartsWith("These historical user statements", StringComparison.Ordinal))?.Text;
        var History = Messages.LastOrDefault(Message => Message.Role == ChatRole.User && Message.Text.StartsWith("Historical excerpts supplied", StringComparison.Ordinal))?.Text;
        var Request = Messages.LastOrDefault(Message => Message.Role == ChatRole.User)?.Text ?? string.Empty;
        var CSharp = Request.Trim().Equals("demo csharp automation", StringComparison.OrdinalIgnoreCase);
        if (CSharp || Request.Trim().Equals("demo automation", StringComparison.OrdinalIgnoreCase))
        {
            // An explicit fixture exercises real tool binding, authorization, persistence and scheduling.
            // It is not an evaluation of live model authoring quality.
            var Last = Messages.LastOrDefault();
            if (Last?.Role == ChatRole.Tool)
            {
                var Result = Last.Contents.OfType<FunctionResultContent>().LastOrDefault()?.Result;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"Synthetic automation tool result: {Result}")));
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(Guid.NewGuid().ToString("N"), "save_automation", new Dictionary<string, object?>
            {
                ["name"] = CSharp ? "Synthetic C# automation" : "Synthetic chat automation",
                ["source"] = CSharp
                    ? """{"steps":[{"id":"program","action":"csharp","arguments":{"source":"Console.Write(Console.In.ReadToEnd().ToUpperInvariant());","input":"created by c#"}},{"id":"report","action":"save_report","arguments":{"title":"Program report","content":"{{steps.program}}"}}]}"""
                    : """{"steps":[{"id":"message","action":"text","arguments":{"text":"Created through chat"}},{"id":"report","action":"save_report","arguments":{"title":"Chat automation report","content":"{{steps.message}}"}}]}""",
                ["automationId"] = null, ["expectedVersion"] = null, ["executeAt"] = null, ["repeatEvery"] = "PT1M"
            })])));
        }
        var Text = Memory is null
            ? "Synthetic demo response. Try: remember that my favorite exercise is deadlift, then ask about my favorite exercise. Upload a small .m4a file to see the fixed two-speaker review example."
            : "Synthetic demo recall from this account's stored conversation:\n" + Memory[(Memory.IndexOf('\n') + 1)..];
        if (History is not null) Text = "Synthetic historical context (retrieval plumbing, not live model reasoning):\n" + History;
        if (Request.Contains("demo cards", StringComparison.OrdinalIgnoreCase)) Text = """
            Here's a small plan to try. These are synthetic examples, and no reminder has been scheduled.
            ```garden-card
            {"kind":"commitment","title":"Return the package","detail":"Bring the receipt. Track this when you're ready."}
            ```
            ```garden-card
            {"kind":"checklist","title":"Before you leave","items":[{"id":"receipt","text":"Pack the receipt"},{"id":"package","text":"Bring the package"}]}
            ```
            ```garden-card
            {"kind":"clarification","title":"When would you like to go?","options":["Tomorrow","This weekend"]}
            ```
            """;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Text)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> Messages, ChatOptions? Options = null, [EnumeratorCancellation] CancellationToken CancellationToken = default)
    {
        var Response = await GetResponseAsync(Messages, Options, CancellationToken);
        foreach (var Message in Response.Messages)
            yield return new ChatResponseUpdate { Role = Message.Role, Contents = Message.Contents };
    }
}
