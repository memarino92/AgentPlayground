using System.Text.Json;

namespace PersonalAgent.Services;

internal static class JevToolResultFormatter
{
    public static string Format(object? Result)
    {
        if (Result is null) return "The tool returned no result.";
        if (Result is string Text) return Text;
        var Json = Result is JsonElement Element ? Element : JsonSerializer.SerializeToElement(Result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (Json.ValueKind == JsonValueKind.String) return Json.GetString()!;
        if (Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty("content", out var Content) && Content.ValueKind == JsonValueKind.Array)
        {
            var Texts = Content.EnumerateArray().Where(Item => Item.ValueKind == JsonValueKind.Object
                && Item.TryGetProperty("type", out var Type) && Type.GetString() == "text" && Item.TryGetProperty("text", out _))
                .Select(Item => Item.GetProperty("text").GetString()).ToArray();
            if (Texts.Length > 0) return (Json.TryGetProperty("isError", out var Error) && Error.ValueKind == JsonValueKind.True ? "Tool reported an error:\n" : "")
                + string.Join("\n\n", Texts);
        }
        return JsonSerializer.Serialize(Json, new JsonSerializerOptions { WriteIndented = true });
    }
}
