using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using PersonalAgent.Models;

namespace PersonalAgent.Services;

// Copy source text and normalize explicit values. Never invent an argument or execute a model-produced payload.
internal static class JevToolArgumentCompiler
{
    public static bool TryCompile(string Message, BoundAgentTool Tool, out AIFunctionArguments Arguments)
    {
        Arguments = new();
        if (Message.Length > 8000 || Message.Any(char.IsControl)) return false;
        var Text = Message.Trim();
        var Key = Tool.Descriptor.Key;
        if (JevRequestRouter.TryCompile(Text, Tool, out Arguments)) return true;
        Arguments = new();
        if (Key == AgentToolKeys.SyncWorkJournal && Match(Text, @"(?:please )?sync my work journal[.!]?").Success)
            Arguments["reason"] = "User requested";
        else if (Key == AgentToolKeys.PublishMobileNotification && Capture(Text, @"(?:notify me|send me a notification): (?<value>.+)", out var Body))
        {
            Arguments["title"] = "Notification";
            Arguments["body"] = Body;
        }
        else if (Key is AgentToolKeys.ScheduleNotification or AgentToolKeys.ScheduleAgentTask)
        {
            var Pattern = Key == AgentToolKeys.ScheduleNotification
                ? @"remind me in (?<count>[0-9]{1,5}) (?<unit>seconds?|minutes?|hours?|days?) to (?<value>.+)"
                : @"schedule (?:an? )?task in (?<count>[0-9]{1,5}) (?<unit>seconds?|minutes?|hours?|days?): (?<value>.+)";
            var Timing = Match(Text, Pattern);
            if (!Timing.Success || !Payload(Timing.Groups["value"].Value)) return false;
            var Count = int.Parse(Timing.Groups["count"].Value, CultureInfo.InvariantCulture);
            var Seconds = Count * (Timing.Groups["unit"].Value.ToLowerInvariant()[0] switch { 's' => 1L, 'm' => 60L, 'h' => 3600L, _ => 86400L });
            if (Seconds is < 1 or > 31536000) return false;
            Arguments["delay"] = $"PT{Seconds.ToString(CultureInfo.InvariantCulture)}S";
            Arguments["executeAt"] = null;
            Arguments["when"] = null;
            Arguments["timeZoneId"] = null;
            if (Key == AgentToolKeys.ScheduleNotification)
            {
                Arguments["title"] = "Reminder";
                Arguments["body"] = Timing.Groups["value"].Value;
            }
            else
            {
                Arguments["instruction"] = Timing.Groups["value"].Value;
                Arguments["notifyOnCompletion"] = true;
            }
        }
        else if (Key == AgentToolKeys.SearchWorkJournal && Capture(Text, @"search my work journal for (?<value>.+)", out var Query))
            Arguments["query"] = Query;
        else if (Key == AgentToolKeys.SearchCoachCheckins && Capture(Text, @"search my coaching notes for (?<value>.+)", out var CoachQuery))
        {
            if (Regex.IsMatch(CoachQuery, @"\b(?:latest|last|recording|filename|historical|compare)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) return false;
            Arguments["query"] = CoachQuery;
            Arguments["exerciseTag"] = null;
            Arguments["fileName"] = null;
            Arguments["recency"] = "recent";
        }
        else if (Key == AgentToolKeys.ListScheduledJobs && Match(Text, @"list my scheduled jobs[.!]?").Success)
        {
            Arguments["status"] = null;
            Arguments["before"] = null;
        }
        else if (Key == AgentToolKeys.GetScheduledJob && JobId(Text, @"show scheduled job (?<id>[0-9a-f-]{36})[.!]?", out var GetId))
            Arguments["jobId"] = GetId;
        else if (Key == AgentToolKeys.CancelScheduledJob && JobId(Text, @"cancel scheduled job (?<id>[0-9a-f-]{36})[.!]?", out var CancelId))
            Arguments["jobId"] = CancelId;
        else if (Key == AgentToolKeys.UpdateScheduledJob)
        {
            var Update = Match(Text, @"update scheduled job (?<id>[0-9a-f-]{36}) instruction: (?<value>.+)");
            if (!Update.Success || !Guid.TryParse(Update.Groups["id"].Value, out var UpdateId)
                || !Payload(Update.Groups["value"].Value)) return false;
            Arguments["jobId"] = UpdateId;
            Arguments["instruction"] = Update.Groups["value"].Value;
            Arguments["title"] = null;
            Arguments["body"] = null;
            Arguments["delay"] = null;
            Arguments["executeAt"] = null;
            Arguments["when"] = null;
            Arguments["timeZoneId"] = null;
            Arguments["notifyOnCompletion"] = null;
        }
        else if (Tool.Source == "TavilyMcp" && Key == AgentToolKeys.Tavily(Tool.Function.Name))
        {
            switch (Tool.Function.Name)
            {
                case "tavily_search" when Capture(Text, @"search the web for (?<value>.+)", out var WebQuery):
                    Arguments["query"] = WebQuery;
                    break;
                case "tavily_research" when Capture(Text, @"research: (?<value>.+)", out var Research):
                    Arguments["input"] = Research;
                    break;
                case "tavily_extract" when Url(Text, "extract", out var ExtractUrl):
                    Arguments["urls"] = new[] { ExtractUrl };
                    break;
                case "tavily_crawl" when Url(Text, "crawl", out var CrawlUrl):
                    Arguments["url"] = CrawlUrl;
                    break;
                case "tavily_map" when Url(Text, "map", out var MapUrl):
                    Arguments["url"] = MapUrl;
                    break;
                default: return false;
            }
        }
        else return false;
        return FitsSchema(Tool.Function.JsonSchema, Arguments);
    }

    private static Match Match(string Text, string Pattern) => Regex.Match(Text, @"\A(?:" + Pattern + @")\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static bool Capture(string Text, string Pattern, out string Value)
    {
        var Result = Match(Text, Pattern);
        Value = Result.Groups["value"].Value;
        return Result.Success && Payload(Value);
    }

    private static bool Payload(string Value) => !string.IsNullOrWhiteSpace(Value) && Value.Length <= 4000
        && !Regex.IsMatch(Value, @"\b(?:and then|then|instead|unless|but first)\b|[\r\n]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static bool JobId(string Text, string Pattern, out Guid Id)
    {
        var Result = Match(Text, Pattern);
        return Guid.TryParse(Result.Groups["id"].Value, out Id) && Result.Success;
    }

    private static bool Url(string Text, string Verb, out string Value)
    {
        Value = "";
        var Result = Match(Text, Verb + @" (?<value>https?://[^\s]+)");
        if (!Result.Success || !Uri.TryCreate(Result.Groups["value"].Value, UriKind.Absolute, out var Parsed)
            || Parsed.Scheme is not ("http" or "https") || Parsed.UserInfo.Length != 0 || Parsed.IsLoopback
            || Uri.CheckHostName(Parsed.Host) != UriHostNameType.Dns || !Parsed.Host.Contains('.')) return false;
        Value = Result.Groups["value"].Value;
        return true;
    }

    // A deliberately small, fail-closed schema checker for the argument shapes above. New constraints fall back to chat.
    internal static bool FitsSchema(JsonElement Schema, AIFunctionArguments Arguments)
    {
        try
        {
            if (Schema.ValueKind != JsonValueKind.Object || !Schema.TryGetProperty("type", out var Type) || Type.GetString() != "object"
                || Schema.EnumerateObject().Any(Property =>
                    Property.Name is not ("type" or "properties" or "required" or "additionalProperties" or "description" or "title" or "$schema"))) return false;
            if (!Schema.TryGetProperty("properties", out var Properties)) return false;
            if (Schema.TryGetProperty("required", out var Required)
                && Required.EnumerateArray().Any(Name => Name.ValueKind != JsonValueKind.String || !Arguments.ContainsKey(Name.GetString()!))) return false;
            return Arguments.All(Pair => Properties.TryGetProperty(Pair.Key, out var Property)
                && FitsValue(Property, JsonSerializer.SerializeToElement(Pair.Value)));
        }
        catch (InvalidOperationException) { return false; }
    }

    private static bool FitsValue(JsonElement Schema, JsonElement Value)
    {
        if (Schema.ValueKind != JsonValueKind.Object) return false;
        foreach (var Property in Schema.EnumerateObject())
        {
            var Valid = Property.Name switch
            {
                "description" or "title" or "default" => true,
                "format" => Property.Value.GetString() == "uuid" && Value.ValueKind == JsonValueKind.String
                    && Guid.TryParse(Value.GetString(), out _),
                "type" => Property.Value.ValueKind == JsonValueKind.Array
                    ? Property.Value.EnumerateArray().Any(Type => IsType(Type.GetString(), Value)) : IsType(Property.Value.GetString(), Value),
                "enum" => Property.Value.EnumerateArray().Any(Item => JsonElement.DeepEquals(Item, Value)),
                "anyOf" => Property.Value.EnumerateArray().Any(Item => FitsValue(Item, Value)),
                "items" => Value.ValueKind == JsonValueKind.Array && Value.EnumerateArray().All(Item => FitsValue(Property.Value, Item)),
                _ => false
            };
            if (!Valid) return false;
        }
        return true;
    }

    private static bool IsType(string? Type, JsonElement Value) => Type switch
    {
        "string" => Value.ValueKind == JsonValueKind.String,
        "null" => Value.ValueKind == JsonValueKind.Null,
        "boolean" => Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => Value.ValueKind == JsonValueKind.Array,
        _ => false
    };
}
