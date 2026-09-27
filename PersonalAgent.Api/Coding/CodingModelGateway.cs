using System.Net.Http.Headers;
using System.Text.Json.Nodes;

using PersonalAgent.Contracts.Coding;
using PersonalAgent.Integrations;

namespace PersonalAgent.Api.Coding;

internal sealed class CodingModelGateway(CodingJobStore Store, CodingJobService Authorization, HttpClient Http)
{
    internal static JsonObject Sanitize(JsonObject Input)
    {
        // No remote context, images, hosted tools, plugin charges, premium tiers or model selection by generated code.
        if (Input["model"]?.GetValue<string>() != CodingJobs.Model || Input["messages"] is not JsonArray Messages || Messages.Count == 0)
            throw new ArgumentException("Unsupported model request.");
        var SafeMessages = new JsonArray();
        foreach (var Message in Messages)
        {
            if (Message is not JsonObject M || M["role"]?.GetValue<string>() is not ("system" or "developer" or "user" or "assistant" or "tool"))
                throw new ArgumentException("Unsupported message.");
            var Content = Message?["content"];
            if (Content is JsonArray Parts && Parts.Any(P => P?["type"]?.GetValue<string>() != "text")) throw new ArgumentException("Text-only requests required.");
            if (Content is not null and not JsonArray && (Content is not JsonValue V || !V.TryGetValue<string>(out _))) throw new ArgumentException("Text-only requests required.");
            var Safe = new JsonObject();
            foreach (var Key in new[] { "role", "name", "tool_call_id", "tool_calls" })
                if (M[Key] is { } Value) Safe[Key] = Value.DeepClone();
            Safe["content"] = Content is JsonArray TextParts
                ? new JsonArray(TextParts.Select(P => (JsonNode)new JsonObject { ["type"] = "text", ["text"] = P!["text"]!.GetValue<string>() }).ToArray())
                : Content?.DeepClone();
            SafeMessages.Add(Safe);
        }
        if (Input["tools"] is JsonArray Tools && Tools.Any(T => T?["type"]?.GetValue<string>() != "function")) throw new ArgumentException("Local function tools only.");
        var Clean = new JsonObject();
        foreach (var Key in new[] { "tools", "tool_choice", "parallel_tool_calls", "temperature", "top_p" })
            if (Input[Key] is { } Value) Clean[Key] = Value.DeepClone();
        Clean["messages"] = SafeMessages;
        Clean["model"] = CodingJobs.Model; Clean["max_tokens"] = 8192; Clean["stream"] = true;
        Clean["provider"] = new JsonObject
        {
            ["only"] = new JsonArray("openai"), ["allow_fallbacks"] = false, ["require_parameters"] = true,
            ["max_price"] = new JsonObject { ["prompt"] = .75, ["completion"] = 4.5, ["request"] = 0 }
        };
        return Clean;
    }

    public async Task RelayAsync(Guid Id, HttpContext Context, CancellationToken Token)
    {
        var Header = Context.Request.Headers.Authorization.ToString();
        if (!Header.StartsWith("Bearer ", StringComparison.Ordinal) || Header.Length != 71) throw new UnauthorizedAccessException();
        var Job = await Store.GetAsync(Id, Token) ?? throw new UnauthorizedAccessException();
        await Authorization.RequireAsync(Job.ActorId, Job.Email, Job.Subject, Token);
        var Current = await Store.SettingsAsync(Token);
        if (!Current.View.Settings.Enabled) throw new UnauthorizedAccessException();
        using var Deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var Input = await Context.Request.ReadFromJsonAsync<JsonObject>(Deadline.Token) ?? throw new ArgumentException();
        var Clean = Sanitize(Input);
        if (!await Store.ReserveModelRequestAsync(Id, Header[7..], Job.Settings.MaxModelRequests, Deadline.Token)) throw new UnauthorizedAccessException();
        // Reserve $0.40 even for failed/uncertain calls, never refund. At capped prices and model's 400k context,
        // max input $0.30 + output $0.036864 is below this reservation. Twelve calls reserve at most $4.80.
        using var Request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");
        Request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Current.Secrets.OpenRouterKey);
        Request.Content = JsonContent.Create(Clean);
        using var Response = await Http.SendAsync(Request, HttpCompletionOption.ResponseHeadersRead, Deadline.Token);
        if (!Response.IsSuccessStatusCode) { Context.Response.StatusCode = 502; return; }
        Context.Response.ContentType = "text/event-stream";
        await using var Source = await Response.Content.ReadAsStreamAsync(Deadline.Token);
        var Buffer = new byte[8192]; var Total = 0; int Count;
        while ((Count = await Source.ReadAsync(Buffer, Deadline.Token)) > 0)
        {
            Total += Count;
            if (Total > 4_000_000) throw new InvalidOperationException("Model response limit exceeded.");
            await Context.Response.Body.WriteAsync(Buffer.AsMemory(0, Count), Deadline.Token);
            await Context.Response.Body.FlushAsync(Deadline.Token);
        }
    }
}
