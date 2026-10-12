using System.Text.Json.Nodes;

namespace PersonalAgent.Api.Coding;

// OpenCode uses Responses natively; keep subscription compatibility at the trusted gateway.
internal static class CodingSubscriptionRequest
{
    public static JsonObject Sanitize(JsonObject Input, string Model)
    {
        if (Input["model"]?.GetValue<string>() != Model || Input["input"] is not JsonArray Items || Items.Count == 0)
            throw new ArgumentException("The coding request must use its selected model and replay text context.");
        var History = new JsonArray();
        foreach (var Item in Items)
        {
            if (Item is not JsonObject Value) throw new ArgumentException("Unsupported coding input.");
            var Type = Value["type"]?.GetValue<string>() ?? "message";
            var Safe = new JsonObject { ["type"] = Type };
            switch (Type)
            {
                case "message":
                    var Role = Value["role"]?.GetValue<string>();
                    if (Role is not ("system" or "developer" or "user" or "assistant")) throw new ArgumentException("Unsupported role.");
                    Safe["role"] = Role == "system" ? "developer" : Role;
                    if (Value["content"] is JsonValue Text && Text.TryGetValue<string>(out var Content)) Safe["content"] = Content;
                    else if (Value["content"] is JsonArray Parts)
                    {
                        var TextParts = new JsonArray();
                        foreach (var Part in Parts)
                        {
                            if (Part?["type"]?.GetValue<string>() is not ("input_text" or "output_text"))
                                throw new ArgumentException("Text-only coding input required.");
                            TextParts.Add(new JsonObject { ["type"] = Part["type"]!.DeepClone(), ["text"] = Part["text"]!.DeepClone() });
                        }
                        Safe["content"] = TextParts;
                    }
                    else throw new ArgumentException("Text context required.");
                    break;
                case "function_call":
                    Copy(Value, Safe, "call_id", "name", "arguments");
                    Safe["namespace"] = "coding";
                    break;
                case "function_call_output":
                    Copy(Value, Safe, "call_id");
                    if (Value["output"] is not JsonValue Output || !Output.TryGetValue<string>(out _))
                        throw new ArgumentException("Text tool output required.");
                    Safe["output"] = Output.DeepClone();
                    break;
                case "reasoning":
                    // Encrypted reasoning can be replayed without server-side response storage.
                    Copy(Value, Safe, "id", "encrypted_content");
                    Safe["summary"] = new JsonArray();
                    break;
                default: throw new ArgumentException("Only text and local function context are supported.");
            }
            History.Add(Safe);
        }
        var Result = new JsonObject { ["model"] = Model, ["input"] = History, ["store"] = false, ["stream"] = true,
            ["include"] = new JsonArray("reasoning.encrypted_content") };
        if (Input["instructions"] is JsonValue Instructions && Instructions.TryGetValue<string>(out _)) Result["instructions"] = Instructions.DeepClone();
        if (Input["tools"] is JsonArray Tools && Tools.Count > 0)
        {
            var Functions = new JsonArray();
            foreach (var Tool in Tools)
            {
                if (Tool is not JsonObject Function || Function["type"]?.GetValue<string>() != "function")
                    throw new ArgumentException("Local function tools only.");
                var Safe = new JsonObject { ["type"] = "function", ["strict"] = false };
                Copy(Function, Safe, "name", "description", "parameters");
                Functions.Add(Safe);
            }
            Result["tools"] = new JsonArray(new JsonObject { ["type"] = "namespace", ["name"] = "coding",
                ["description"] = "Local repository tools executed inside this coding job's isolated sandbox.", ["tools"] = Functions });
            if (Input["tool_choice"] is JsonValue Choice && Choice.TryGetValue<string>(out var Mode) && Mode is "auto" or "required" or "none")
                Result["tool_choice"] = Mode;
            else if (Input["tool_choice"] is JsonObject Required && Required["type"]?.GetValue<string>() == "function")
                Result["tool_choice"] = new JsonObject { ["type"] = "function", ["name"] = Required["name"]!.DeepClone(), ["namespace"] = "coding" };
        }
        return Result;
    }

    private static void Copy(JsonObject Source, JsonObject Target, params string[] Keys)
    {
        foreach (var Key in Keys)
            if (Source[Key] is { } Value) Target[Key] = Value.DeepClone();
    }
}

internal sealed class CodingGatewayException(int Status, string Message) : Exception(Message)
{
    public int Status { get; } = Status;
}
