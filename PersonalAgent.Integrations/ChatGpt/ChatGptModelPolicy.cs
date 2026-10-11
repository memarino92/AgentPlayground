using System.Text.Json;

namespace PersonalAgent.Integrations.ChatGpt;

public static class ChatGptModelPolicy
{
    public static IReadOnlySet<string> Parse(string Value)
    {
        string[]? models;
        try { models = JsonSerializer.Deserialize<string[]>(Value); }
        catch (JsonException) { throw new FormatException("Use a JSON array of reviewed model slugs."); }
        if (models is null || models.Length > 100 || models.Any(Model => string.IsNullOrWhiteSpace(Model)
            || Model.Length > 200 || Model.Any(char.IsWhiteSpace)))
            throw new FormatException("Use up to 100 nonempty model slugs of at most 200 characters without spaces.");
        return models.ToHashSet(StringComparer.Ordinal);
    }
}
