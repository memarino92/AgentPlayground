using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;
using PersonalAgent.Contracts.Automations;

namespace PersonalAgent.Api.Automations;

internal sealed class AutomationRecipes(IAgentToolRegistry Registry)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };
    internal static readonly HashSet<string> AllowedTools =
    [AgentToolKeys.GetCurrentDateTime, AgentToolKeys.SearchCoachCheckins, AgentToolKeys.SearchWorkJournal];
    private static readonly Regex IdPattern = new("^[a-z][a-z0-9_]{0,39}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Placeholder = new(@"\{\{([^{}]+)\}\}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public object Catalog(IServiceProvider Services, AgentAccessContext Access) => new
    {
        format = "{\"steps\":[{\"id\":\"hello\",\"action\":\"text\",\"arguments\":{\"text\":\"Hello\"}}]}",
        templates = "String values support {{steps.ID}} (whole earlier output), {{run.scheduledAt}}, {{run.id}}. Optional when: {step: earlierId, equals: exactOutput}. No code, expressions, HTTP, shell, or model calls.",
        actions = new object[]
        {
            new { action = "text", arguments = new { text = "literal or template" } },
            new { action = "save_report", arguments = new { title = "report title", content = "literal or template" } },
            new { action = "notify", arguments = new { title = "notification title", body = "literal or template" }, outcome = "Queued for existing push delivery; not confirmed device receipt" },
            new { action = "tool", arguments = new { tool = "registered key", inputs = new { } }, tools = Registry.GetRegistrations()
                .Where(T => AllowedTools.Contains(T.Descriptor.Key)).Select(T => new { key = T.Descriptor.Key, schema = T.CreateFunction(Services, Access).JsonSchema }).ToArray() }
        },
        limits = new { steps = 20, sourceCharacters = 32768, outputCharacters = 65536 }
    };

    public AutomationRecipe Parse(string Source)
    {
        if (string.IsNullOrWhiteSpace(Source) || Source.Length > 32768) throw new ArgumentException("Recipe source must contain 1–32768 characters.");
        AutomationRecipe Recipe;
        try { Recipe = JsonSerializer.Deserialize<AutomationRecipe>(Source, Json) ?? throw new JsonException(); }
        catch (JsonException) { throw new ArgumentException("Recipe must match the catalog JSON schema."); }
        if (Recipe.Steps is null || Recipe.Steps.Count is < 1 or > 20) throw new ArgumentException("A recipe needs 1–20 steps.");
        var Prior = new HashSet<string>();
        foreach (var Step in Recipe.Steps)
        {
            if (Step is null || Step.Id is null || !IdPattern.IsMatch(Step.Id) || Prior.Contains(Step.Id))
                throw new ArgumentException("Step IDs must be unique lowercase identifiers starting with a letter.");
            if (Step.Arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("Step arguments must be an object.");
            string[] Keys = Step.Action switch
            {
                "text" => ["text"], "save_report" => ["title", "content"], "notify" => ["title", "body"], "tool" => ["tool", "inputs"],
                _ => throw new ArgumentException("Unknown action. Discover supported automation actions first.")
            };
            if (Step.Arguments.EnumerateObject().Select(P => P.Name).Distinct().Count() != Step.Arguments.EnumerateObject().Count()
                || Step.Arguments.EnumerateObject().Any(P => !Keys.Contains(P.Name)) || Keys.Any(K => !Step.Arguments.TryGetProperty(K, out _)))
                throw new ArgumentException($"Invalid arguments for {Step.Action}.");
            foreach (var Key in Keys.Where(K => K != "inputs"))
                if (Step.Arguments.GetProperty(Key).ValueKind != JsonValueKind.String) throw new ArgumentException($"{Key} must be a string.");
            if (Step.Action == "tool" && (!AllowedTools.Contains(Step.Arguments.GetProperty("tool").GetString()!)
                || Step.Arguments.GetProperty("inputs").ValueKind != JsonValueKind.Object))
                throw new ArgumentException("Only cataloged read tools with an inputs object can be automated.");
            if (Step.When is { } Condition && (!Prior.Contains(Condition.Step) || Condition.Expected is null))
                throw new ArgumentException("Conditions must compare an earlier step output.");
            foreach (Match Match in Placeholder.Matches(Step.Arguments.GetRawText()))
            {
                var Key = Match.Groups[1].Value;
                if (Key is not ("run.id" or "run.scheduledAt") && !(Key.StartsWith("steps.", StringComparison.Ordinal) && Prior.Contains(Key[6..])))
                    throw new ArgumentException("Templates may only reference an earlier step or run metadata.");
            }
            Prior.Add(Step.Id);
        }
        return Recipe;
    }

    public static JsonElement Resolve(AutomationStep Step, AutomationRun Run, IReadOnlyList<AutomationStepExecution> Earlier)
    {
        string Expand(string Value) => Placeholder.Replace(Value, Match => Match.Groups[1].Value switch
        {
            "run.id" => Run.CorrelationId.ToString(), "run.scheduledAt" => Run.ScheduledAt.ToString("O"),
            var Key => Earlier.Single(E => E.StepId == Key[6..]).Output ?? ""
        });
        JsonNode? Walk(JsonNode? Node) => Node switch
        {
            JsonObject Object => new JsonObject(Object.Select(P => KeyValuePair.Create(P.Key, Walk(P.Value))).ToArray()),
            JsonArray Array => new JsonArray(Array.Select(Walk).ToArray()),
            JsonValue Value when Value.TryGetValue<string>(out var Text) => JsonValue.Create(Expand(Text)),
            _ => Node?.DeepClone()
        };
        var Resolved = Walk(JsonNode.Parse(Step.Arguments.GetRawText()));
        var Text = Resolved!.ToJsonString();
        if (Text.Length > 131072) throw new ArgumentException("Expanded arguments exceed 128 KiB.");
        return JsonSerializer.SerializeToElement(Resolved);
    }

    public async Task ValidateToolsAsync(AutomationRecipe Recipe, IServiceProvider Services, AgentAccessContext Access, ToolAccessService Permissions, CancellationToken Token)
    {
        foreach (var Step in Recipe.Steps)
        {
            if (Step.Action == "notify" && !await Permissions.IsAllowedAsync(Access.Role, AgentToolKeys.PublishMobileNotification, Token))
                throw new UnauthorizedAccessException("Notification permission is required.");
            if (Step.Action != "tool") continue;
            var Key = Step.Arguments.GetProperty("tool").GetString()!;
            if (!await Permissions.IsAllowedAsync(Access.Role, Key, Token)) throw new UnauthorizedAccessException("An action is unavailable or unauthorized.");
            var Function = Registry.GetRegistrations().Single(T => T.Descriptor.Key == Key).CreateFunction(Services, Access);
            var Inputs = Step.Arguments.GetProperty("inputs");
            var Schema = Function.JsonSchema;
            if (Schema.TryGetProperty("required", out var Required) && Required.EnumerateArray().Any(P => !Inputs.TryGetProperty(P.GetString()!, out _)))
                throw new ArgumentException($"Missing required inputs for {Key}.");
            if (Schema.TryGetProperty("properties", out var Properties) && Inputs.EnumerateObject().Any(P => !Properties.TryGetProperty(P.Name, out _)))
                throw new ArgumentException($"Unknown inputs for {Key}.");
        }
    }
}
