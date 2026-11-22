using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;
using PersonalAgent.Contracts.Automations;
using PersonalAgent.Contracts.Coding;

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
        templates = "String values support {{steps.ID}} (whole earlier output), {{run.scheduledAt}}, {{run.id}}. Optional when: {step: earlierId, equals: exactOutput}. Program source and dependency/tool declarations are literal; only its input is expanded. Shell and C# programs can access the public Internet on Railway. Write binary or multi-file deliverables to /artifacts for download after completion. For a phone display write a self-contained HTML document starting with <!doctype html> to stdout.",
        actions = new object[]
        {
            new { action = "text", arguments = new { text = "literal or template" } },
            new { action = "dockerfile", arguments = new { source = "FROM python:3.13-slim\nCMD [\"python\",\"-c\",\"import sys; print(sum(map(float,sys.stdin)))\"]", input = "1\n2" }, requirements = "Railway builds this Dockerfile with public Internet and an empty file context in a disposable VM. Embed code in the Dockerfile. CMD or ENTRYPOINT runs as UID 65532 with stdin. Inline assets in HTML output. Build up to eight minutes, execution five minutes, combined runtime output 32 KiB." },
            new { action = "python", arguments = new { source = "import statistics\nstatistics.mean([3, 9])", input = "" }, requirements = "Fresh Python interpreter with numpy, pandas and matplotlib. Put files in /artifacts. For conversation state, call run_python." },
            new { action = "shell", arguments = new { source = "wc -w", input = "words to count" }, requirements = "Owner sandbox program permission. POSIX /bin/sh with stdin, stdout and optional declared read tools. Same runtime and output budgets as C#." },
            new { action = "csharp", arguments = new { source = "Console.WriteLine(Console.In.ReadToEnd().ToUpperInvariant());", input = "literal or template" },
                requirements = "Owner only; administrator must enable Local:csharp_automation and configure the Railway runner. Single C# source file, net11.0; no project/property directives. Internet enabled, 512 MiB RAM, 95 second build/run watchdog, 32 KiB output. Optional packages: [ID@exact.version] require packageLock (NuGet packages.lock.json string) and administrator approval of every resolved dependency. Optional tools: [registered read-tool key] declares gateway capabilities. POST JSON {operationId: unique id, tool: registered key, inputs: object} to AUTOMATION_GATEWAY_URL with Bearer AUTOMATION_GATEWAY_TOKEN; response is {output: string}. No database/provider credentials. Use subsequent recipe steps for domain writes. Internet traffic is not intercepted by approvals." },
            new { action = "save_report", arguments = new { title = "report title", content = "literal or template" } },
            new { action = "notify", arguments = new { title = "notification title", body = "literal or template" }, outcome = "Queued for existing push delivery; not confirmed device receipt" },
            new { action = "tool", arguments = new { tool = "registered key", inputs = new { } }, tools = Registry.GetRegistrations()
                .Where(T => AllowedTools.Contains(T.Descriptor.Key)).Select(T => new { key = T.Descriptor.Key, schema = T.CreateFunction(Services, Access).JsonSchema }).ToArray() }
        },
        supervisedActions = new
        {
            use = "Run these actions in an explicitly requested one-time supervised workflow.",
            actions = new object[]
            {
                new { action = "public_research", arguments = new { questions = new[] { "First independent question", "Second independent question" } } },
                new { action = "coding_job", arguments = new { instruction = "Literal repository change", evidenceSteps = new[] { "earlier_research_id" } },
                    requirements = "At most one coding job. Evidence is optional and can reference earlier public research or literal text steps." },
                new { action = "synthesize", arguments = new { question = "Summarize what each branch achieved and what remains.", sourceSteps = new[] { "earlier_step_id" } } }
            }
        },
        limits = new { steps = 20, sourceCharacters = 32768, outputCharacters = 65536 }
    };

    public AutomationRecipe Parse(string Source, bool Supervised = false)
    {
        if (string.IsNullOrWhiteSpace(Source) || Source.Length > 32768) throw new ArgumentException("Recipe source must contain 1–32768 characters.");
        AutomationRecipe Recipe;
        try { Recipe = JsonSerializer.Deserialize<AutomationRecipe>(Source, Json) ?? throw new JsonException(); }
        catch (JsonException) { throw new ArgumentException("Recipe must match the catalog JSON schema."); }
        Recipe = Recipe with { Supervised = Supervised || Recipe.Supervised };
        if (!Recipe.Supervised && Recipe.SupervisedModelId is not null) throw new ArgumentException("Model selection applies only to supervised workflows.");
        if (Recipe.Steps is null || Recipe.Steps.Count is < 1 or > 20) throw new ArgumentException("A recipe needs 1–20 steps.");
        var Prior = new Dictionary<string, AutomationStep>();
        foreach (var Step in Recipe.Steps)
        {
            if (Step is null || Step.Id is null || !IdPattern.IsMatch(Step.Id) || Prior.ContainsKey(Step.Id))
                throw new ArgumentException("Step IDs must be unique lowercase identifiers starting with a letter.");
            if (Step.Arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("Step arguments must be an object.");
            string[] Keys = Step.Action switch
            {
                "text" => ["text"], "save_report" => ["title", "content"], "notify" => ["title", "body"], "tool" => ["tool", "inputs"], "csharp" or "shell" or "dockerfile" or "python" => ["source", "input"],
                "coding_job" when Recipe.Supervised => ["instruction"],
                "public_research" when Recipe.Supervised => ["questions"],
                "synthesize" when Recipe.Supervised => ["question", "sourceSteps"],
                _ => throw new ArgumentException("Unknown action. Discover supported automation actions first.")
            };
            if (Step.Arguments.EnumerateObject().Select(P => P.Name).Distinct().Count() != Step.Arguments.EnumerateObject().Count()
                || Step.Arguments.EnumerateObject().Any(P => !Keys.Contains(P.Name) && !OptionalArgument(Step.Action, P.Name))
                || Keys.Any(K => !Step.Arguments.TryGetProperty(K, out _)))
                throw new ArgumentException($"Invalid arguments for {Step.Action}.");
            foreach (var Key in Keys.Where(K => K is not ("inputs" or "questions" or "sourceSteps")))
                if (Step.Arguments.GetProperty(Key).ValueKind != JsonValueKind.String) throw new ArgumentException($"{Key} must be a string.");
            if (Step.Action == "coding_job") ValidateCodingStep(Step.Arguments, Prior);
            if (Step.Action == "public_research") ResearchQuestions(Step.Arguments);
            if (Step.Action == "synthesize") ValidateSynthesis(Step.Arguments, Prior);
            if (Step.Action == "python" && Step.Arguments.TryGetProperty("imageId", out var Image)
                && (Image.ValueKind != JsonValueKind.String || !Regex.IsMatch(Image.GetString()!, @"\Asha256:[0-9a-f]{64}\z")))
                throw new ArgumentException("Python imageId must be an immutable image digest.");
            if (Step.Action == "tool" && (!AllowedTools.Contains(Step.Arguments.GetProperty("tool").GetString()!)
                || Step.Arguments.GetProperty("inputs").ValueKind != JsonValueKind.Object))
                throw new ArgumentException("Only cataloged read tools with an inputs object can be automated.");
            if (AutomationPrograms.IsProgram(Step.Action) && (string.IsNullOrWhiteSpace(Step.Arguments.GetProperty("source").GetString())
                || Step.Arguments.GetProperty("source").GetString()!.Length > AutomationPrograms.MaxSource
                || Step.Arguments.GetProperty("input").GetString()!.Length > AutomationPrograms.MaxInput))
                throw new ArgumentException("Program source must contain 1–24000 characters; input is limited to 65536 characters.");
            if (Step.Action != "csharp" && (Step.Arguments.TryGetProperty("packages", out _) || Step.Arguments.TryGetProperty("packageLock", out _)))
                throw new ArgumentException("Managed NuGet dependencies apply only to C# programs.");
            if (AutomationPrograms.IsProgram(Step.Action))
            {
                ValidateProgram(Step.Arguments);
                SandboxInputFile.Validate(InputFiles(Step.Arguments));
            }
            if (Step.When is { } Condition && (Condition.Step is null || !Prior.ContainsKey(Condition.Step) || Condition.Expected is null))
                throw new ArgumentException("Conditions must compare an earlier step output.");
            foreach (Match Match in Placeholder.Matches(TemplateSource(Step)))
            {
                var Key = Match.Groups[1].Value;
                if (Key is not ("run.id" or "run.scheduledAt") && !(Key.StartsWith("steps.", StringComparison.Ordinal) && Prior.ContainsKey(Key[6..])))
                    throw new ArgumentException("Templates may only reference an earlier step or run metadata.");
            }
            Prior.Add(Step.Id, Step);
        }
        return Recipe;
    }

    private static bool OptionalArgument(string Action, string Name) => Action switch
    {
        "coding_job" => Name == "evidenceSteps",
        "python" when Name == "imageId" => true,
        _ => AutomationPrograms.IsProgram(Action) && Name is "inputFiles" or "packages" or "packageLock" or "tools"
    };

    private static bool UsesLiteralArguments(string Action) => Action is "coding_job" or "public_research" or "synthesize";

    private static string TemplateSource(AutomationStep Step) => UsesLiteralArguments(Step.Action) ? ""
        : AutomationPrograms.IsProgram(Step.Action) ? Step.Arguments.GetProperty("input").GetString()! : Step.Arguments.GetRawText();

    private static void ValidateCodingStep(JsonElement Arguments, IReadOnlyDictionary<string, AutomationStep> Prior)
    {
        if (Prior.Values.Any(Step => Step.Action == "coding_job")) throw new ArgumentException("A supervised workflow may start one coding job.");
        var Instruction = Arguments.GetProperty("instruction").GetString();
        if (string.IsNullOrWhiteSpace(Instruction) || Instruction.Length > 8000)
            throw new ArgumentException("A literal coding instruction of 1–8000 characters is required.");
        if (!Arguments.TryGetProperty("evidenceSteps", out var Evidence)) return;
        if (Evidence.ValueKind != JsonValueKind.Array || Evidence.GetArrayLength() > 8)
            throw new ArgumentException("Coding evidenceSteps accepts at most eight earlier public research or text step IDs.");
        var Used = new HashSet<string>();
        foreach (var Item in Evidence.EnumerateArray())
            if (Item.ValueKind != JsonValueKind.String || Item.GetString() is not { } Id || !Used.Add(Id)
                || !Prior.TryGetValue(Id, out var Step) || Step.Action is not ("public_research" or "text")
                || (Step.Action == "text" && Placeholder.IsMatch(Step.Arguments.GetProperty("text").GetString()!)))
                throw new ArgumentException("Coding evidenceSteps must reference distinct earlier public research or literal text steps.");
    }

    internal static string[] ResearchQuestions(JsonElement Arguments)
    {
        var Questions = Arguments.GetProperty("questions");
        if (Questions.ValueKind != JsonValueKind.Array || Questions.EnumerateArray().Any(Q => Q.ValueKind != JsonValueKind.String))
            throw new ArgumentException("Public research questions must be an array of strings.");
        var Values = Questions.EnumerateArray().Select(Q => Q.GetString()!).ToArray();
        if (ResearchSwarm.ValidateQuestions(Values) is { } Error) throw new ArgumentException(Error);
        return Values;
    }

    private static void ValidateSynthesis(JsonElement Arguments, IReadOnlyDictionary<string, AutomationStep> Prior)
    {
        var Question = Arguments.GetProperty("question").GetString();
        var Sources = Arguments.GetProperty("sourceSteps");
        if (string.IsNullOrWhiteSpace(Question) || Question.Length > 1200 || Sources.ValueKind != JsonValueKind.Array
            || Sources.GetArrayLength() is < 1 or > 8)
            throw new ArgumentException("Synthesis needs a literal question of 1–1200 characters and 1–8 earlier step IDs.");
        var Used = new HashSet<string>();
        foreach (var Item in Sources.EnumerateArray())
            if (Item.ValueKind != JsonValueKind.String || Item.GetString() is not { } Id || !Used.Add(Id) || !Prior.ContainsKey(Id))
                throw new ArgumentException("Synthesis sourceSteps must reference distinct earlier steps.");
    }

    public static SandboxInputFile[] InputFiles(JsonElement Arguments)
    {
        if (!Arguments.TryGetProperty("inputFiles", out var Files)) return [];
        try { return Files.Deserialize<SandboxInputFile[]>(Json) ?? throw new ArgumentException("inputFiles must be an array."); }
        catch (JsonException) { throw new ArgumentException("Invalid inputFiles array."); }
    }

    public static JsonElement Resolve(AutomationStep Step, AutomationRun Run, IReadOnlyList<AutomationStepExecution> Earlier)
    {
        if (UsesLiteralArguments(Step.Action)) return Step.Arguments;
        string Expand(string Value) => Placeholder.Replace(Value, Match => Match.Groups[1].Value switch
        {
            "run.id" => Run.CorrelationId.ToString(), "run.scheduledAt" => Run.ScheduledAt.ToString("O"),
            var Key => Earlier.Single(E => E.StepId == Key[6..]).Output ?? ""
        });
        JsonNode? Walk(JsonNode? Node) => Node switch
        {
            JsonObject Object => new JsonObject(Object.Select(P => KeyValuePair.Create(P.Key,
                AutomationPrograms.IsProgram(Step.Action) && P.Key != "input" ? P.Value?.DeepClone() : Walk(P.Value))).ToArray()),
            JsonArray Array => new JsonArray(Array.Select(Walk).ToArray()),
            JsonValue Value when Value.TryGetValue<string>(out var Text) => JsonValue.Create(Expand(Text)),
            _ => Node?.DeepClone()
        };
        var Resolved = Walk(JsonNode.Parse(Step.Arguments.GetRawText()));
        var Text = Resolved!.ToJsonString();
        if (Text.Length > 131072) throw new ArgumentException("Expanded arguments exceed 128 KiB.");
        return JsonSerializer.SerializeToElement(Resolved);
    }

    public async Task ValidateToolsAsync(AutomationRecipe Recipe, IServiceProvider Services, AgentAccessContext Access, ToolAccessService Permissions, CancellationToken Token, bool Scratch = false)
    {
        if (Scratch && (Recipe.Steps.Count != 1 || Recipe.Steps[0].Action != "python"
            || Recipe.Steps[0].Arguments.EnumerateObject().Any(P => P.Name is not ("source" or "input" or "inputFiles"))))
            throw new ArgumentException("A scratchpad cell requires a single Python calculation.");
        foreach (var Step in Recipe.Steps)
        {
            if (Step.Action == "coding_job" && (!Recipe.Supervised || Access.Role != AgentRoles.Owner
                || !await Permissions.IsAllowedAsync(Access.Role, CodingJobs.Permission, Token)))
                throw new UnauthorizedAccessException("Current owner access and coding-job permission are required.");
            if (Step.Action == "public_research")
            {
                var Search = ResearchSwarm.FindSearch(Services.GetRequiredService<ITavilyMcpToolProvider>());
                if (!Recipe.Supervised || Access.Role != AgentRoles.Owner || Search is null
                    || !await Permissions.IsAllowedAsync(Access.Role, AgentToolKeys.ResearchSwarm, Token)
                    || !await Permissions.IsAllowedAsync(Access.Role, AgentToolKeys.Tavily(Search.Name), Token))
                    throw new UnauthorizedAccessException("Public research requires current owner access, research permission and Tavily search.");
            }
            if (AutomationPrograms.IsProgram(Step.Action) && (Access.Role != AgentRoles.Owner
                || !await Permissions.IsAllowedAsync(Access.Role, Scratch ? AutomationPrograms.ScratchPermission : AutomationPrograms.PermissionKey, Token)))
                throw new UnauthorizedAccessException("Sandbox programs require owner access and the administrator-enabled sandbox program permission.");
            if (AutomationPrograms.IsProgram(Step.Action) && Step.Arguments.TryGetProperty("tools", out var Tools))
                foreach (var Tool in Tools.EnumerateArray())
                    if (!await Permissions.IsAllowedAsync(Access.Role, Tool.GetString()!, Token)) throw new UnauthorizedAccessException("A declared tool is unauthorized.");
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

    internal static string[] Packages(JsonElement Arguments) => Arguments.TryGetProperty("packages", out var Packages)
        ? Packages.EnumerateArray().Select(P => P.GetString()!).ToArray() : [];

    internal static string[] LockedPackages(string PackageLock) => AutomationPackageLock.ResolvedPackages(PackageLock);

    private static void ValidateProgram(JsonElement Arguments)
    {
        if (Arguments.TryGetProperty("tools", out var Tools) && (Tools.ValueKind != JsonValueKind.Array || Tools.GetArrayLength() > 3
            || Tools.EnumerateArray().Any(T => T.ValueKind != JsonValueKind.String || !AllowedTools.Contains(T.GetString()!))))
            throw new ArgumentException("Declare only cataloged read tools.");
        if (Arguments.TryGetProperty("packages", out var Items) && (Items.ValueKind != JsonValueKind.Array || Items.GetArrayLength() > 20
            || Items.EnumerateArray().Any(P => P.ValueKind != JsonValueKind.String || !Regex.IsMatch(P.GetString()!, @"\A[A-Za-z0-9_.-]{1,100}@[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?\z"))))
            throw new ArgumentException("Packages must use exact ID@version entries.");
        var Declared = Packages(Arguments);
        if (Declared.Length == 0)
        {
            if (Arguments.TryGetProperty("packageLock", out _)) throw new ArgumentException("A package lock requires declared packages.");
            return;
        }
        if (!Arguments.TryGetProperty("packageLock", out var Lock) || Lock.ValueKind != JsonValueKind.String || Lock.GetString()!.Length > 20000)
            throw new ArgumentException("Packages require a bounded NuGet packageLock string for locked restore.");
        var Resolved = LockedPackages(Lock.GetString()!);
        if (Declared.Any(P => !Resolved.Contains(P, StringComparer.OrdinalIgnoreCase))) throw new ArgumentException("Declared package versions must match the lock.");
    }
}
