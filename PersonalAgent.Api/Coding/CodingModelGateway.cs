using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using PersonalAgent.Contracts.Coding;
using PersonalAgent.Integrations;
using PersonalAgent.Integrations.ChatGpt;
using PersonalAgent.Api.Services;

namespace PersonalAgent.Api.Coding;

internal sealed class CodingModelGateway(CodingJobStore Store, CodingJobService Authorization, HttpClient Http,
    ChatGptConnectionStore Connections, ChatGptModelCatalog Models, ILogger<CodingModelGateway> Logger)
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

    public async Task RelayAsync(Guid Id, HttpContext Context, CancellationToken Token, bool Responses = false)
    {
        var Header = Context.Request.Headers.Authorization.ToString();
        if (!Header.StartsWith("Bearer ", StringComparison.Ordinal) || Header.Length != 71) throw new UnauthorizedAccessException();
        var Job = await Store.GetAsync(Id, Token) ?? throw new UnauthorizedAccessException();
        if (!await Store.AuthorizeModelRequestAsync(Id, Header[7..], Token)) throw new UnauthorizedAccessException();
        await Authorization.RequireAsync(Job.ActorId, Job.Email, Job.Subject, Token);
        var Current = await Store.SettingsAsync(Token);
        if (!Current.View.Settings.Enabled) throw new UnauthorizedAccessException();
        var Subscription = CodingJobs.TrySubscriptionModel(Job.Settings.ModelId, out var Connection, out var Slug);
        if (Responses != Subscription) throw new ArgumentException("Use the selected coding provider's protocol.");
        using var Deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var Remaining = Job.Deadline - DateTimeOffset.UtcNow;
        if (Remaining <= TimeSpan.Zero) throw new UnauthorizedAccessException();
        var RequestLimit = TimeSpan.FromMinutes(Subscription ? 10 : 3);
        Deadline.CancelAfter(Remaining < RequestLimit ? Remaining : RequestLimit);
        var Input = await Context.Request.ReadFromJsonAsync<JsonObject>(Deadline.Token) ?? throw new ArgumentException();
        var Clean = Subscription ? CodingSubscriptionRequest.Sanitize(Input, Slug) : Sanitize(Input);
        var Credential = Current.Secrets.OpenRouterKey;
        if (Subscription)
        {
            if (!(await Models.GetModelsAsync(Job.ActorId, Deadline.Token)).Any(Model => Model.Id == Job.Settings.ModelId))
                throw new CodingGatewayException(403, "The selected ChatGPT model is unavailable. Reconnect or review model access in Settings.");
            var Saved = await Connections.GetCredentialsAsync(Job.ActorId, Connection, Deadline.Token);
            if (!ChatGptOAuth.PlanEnabled(Saved.Scope)) throw new CodingGatewayException(403, "Reconnect and authorize ChatGPT plan usage in Settings.");
            Credential = Saved.AccessToken;
        }
        if (!await Store.ReserveModelRequestAsync(Id, Header[7..], Job.Settings.MaxModelRequests, Deadline.Token))
        {
            var Latest = await Store.GetAsync(Id, Deadline.Token);
            if (Latest?.Status == "Running" && Latest.Deadline > DateTimeOffset.UtcNow && Latest.ModelRequests >= Job.Settings.MaxModelRequests)
                throw new CodingGatewayException(429, "Coding model request limit exhausted. Increase the limit in Platform coding settings and start a new job.");
            throw new UnauthorizedAccessException();
        }
        // Reserve $0.40 even for failed/uncertain calls, never refund. At capped prices and model's 400k context,
        // max input $0.30 + output $0.036864 is below this reservation. Twelve calls reserve at most $4.80.
        using var Request = new HttpRequestMessage(HttpMethod.Post, Subscription ? ChatGptOAuth.Resource + "/responses" : "https://openrouter.ai/api/v1/chat/completions");
        Request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Credential);
        Request.Content = JsonContent.Create(Clean);
        var Started = Stopwatch.GetTimestamp();
        using var Response = await Http.SendAsync(Request, HttpCompletionOption.ResponseHeadersRead, Deadline.Token);
        Logger.LogInformation(new EventId(4405), "Coding inference {JobId} via {Provider} model {Model} returned HTTP {Status} after {ElapsedMs}ms",
            Id, Subscription ? "ChatGPT" : "OpenRouter", Subscription ? Slug : CodingJobs.Model,
            (int)Response.StatusCode, Stopwatch.GetElapsedTime(Started).TotalMilliseconds);
        if (!Response.IsSuccessStatusCode)
        {
            var Failure = await ProviderFailureAsync(Response, Subscription, Deadline.Token);
            Logger.LogWarning(new EventId(4406), "Coding inference {JobId} failed with gateway HTTP {Status}: {Reason}", Id, Failure.Status, Failure.Message);
            throw Failure;
        }
        Context.Response.ContentType = "text/event-stream";
        if (Subscription)
        {
            await RelaySubscriptionStreamAsync(Response, Context, Deadline.Token);
            return;
        }
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

    internal static async Task<CodingGatewayException> ProviderFailureAsync(HttpResponseMessage Response, bool Subscription, CancellationToken Token)
    {
        var Provider = Subscription ? "ChatGPT" : "OpenRouter";
        var Status = Response.StatusCode switch
        {
            System.Net.HttpStatusCode.BadRequest => 400,
            System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => 403,
            System.Net.HttpStatusCode.TooManyRequests => 429,
            _ => 502
        };
        var Parameter = "";
        if (Status == 400)
        {
            // Keep only bounded schema-path metadata, never provider messages, request bodies or tokens.
            await using var Stream = await Response.Content.ReadAsStreamAsync(Token);
            using var Reader = new StreamReader(Stream);
            var Buffer = new char[16384];
            var Count = await Reader.ReadBlockAsync(Buffer.AsMemory(), Token);
            try
            {
                using var Document = JsonDocument.Parse(new string(Buffer, 0, Count));
                if (Document.RootElement.ValueKind == JsonValueKind.Object && Document.RootElement.TryGetProperty("error", out var Error) && Error.ValueKind == JsonValueKind.Object
                    && Error.TryGetProperty("param", out var Field) && Field.ValueKind == JsonValueKind.String)
                {
                    var Path = Field.GetString()!;
                    if (Path.Length <= 160 && Regex.IsMatch(Path,
                        @"\A(?:input|tools|tool_choice|model|store|stream|include|instructions)(?:\[[0-9]{1,4}\]|\.[A-Za-z_][A-Za-z0-9_]*){0,12}\z"))
                        Parameter = $" (parameter: {Path})";
                }
            }
            catch (JsonException) { /* Non-JSON or oversized provider bodies retain a safe generic explanation. */ }
        }
        var Reason = Status switch
        {
            400 => $"{Provider} rejected the coding request format{Parameter}. The provider integration needs attention.",
            403 => Subscription ? "ChatGPT access is unavailable. Reconnect this account in Settings." : "OpenRouter credentials are unavailable. Check provider settings.",
            429 => Subscription ? "ChatGPT plan limits reached. Review usage at https://chatgpt.com/settings/usage." : "OpenRouter coding rate or usage limit reached.",
            _ => $"{Provider} coding inference is temporarily unavailable. Retry later."
        };
        return new(Status, Reason + (Subscription ? " No paid fallback was used." : ""));
    }

    internal static async Task RelaySubscriptionStreamAsync(HttpResponseMessage Response, HttpContext Context, CancellationToken Token)
    {
        await using var Source = await Response.Content.ReadAsStreamAsync(Token);
        using var Reader = new StreamReader(Source);
        var Completed = false;
        var Total = 0;
        var Event = new StringBuilder();
        while (await Reader.ReadLineAsync(Token) is { } Line)
        {
            Total += Encoding.UTF8.GetByteCount(Line) + 1;
            if (Total > 4_000_000) throw new InvalidOperationException("Model response limit exceeded.");
            Event.AppendLine(Line);
            if (Line.Length != 0) continue;
            var Data = Event.ToString().Split('\n').Where(Part => Part.StartsWith("data: ")).Select(Part => Part[6..].TrimEnd('\r'));
            var Payload = string.Join('\n', Data);
            if (Payload.Length > 0 && Payload != "[DONE]")
            {
                using var Document = JsonDocument.Parse(Payload);
                var Type = Document.RootElement.GetProperty("type").GetString();
                if (Type is "response.failed" or "response.incomplete" or "error")
                    throw new CodingGatewayException(502, "ChatGPT did not complete coding inference. Review plan usage at https://chatgpt.com/settings/usage. No paid fallback was used.");
                if (Type == "response.completed") Completed = true;
            }
            await Context.Response.WriteAsync(Event.ToString(), Token);
            await Context.Response.Body.FlushAsync(Token);
            Event.Clear();
        }
        if (!Completed) throw new CodingGatewayException(502, "ChatGPT stream ended before completion. Retry the job; no paid fallback was used.");
    }
}
