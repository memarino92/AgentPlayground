using MassTransit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using PersonalAgent.Configuration;
using PersonalAgent.Models;
using PersonalAgent.Services;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

var json = new JsonSerializerOptions { WriteIndented = true };
var casePath = Environment.GetEnvironmentVariable("COACH_EVAL_CASES") ?? throw new InvalidOperationException("Set COACH_EVAL_CASES.");
if (Environment.GetEnvironmentVariable("COACH_EVAL_RESCORE") is { Length: > 0 } rescoreDirectory)
{
    await EvalRescore.RunAsync(rescoreDirectory, casePath);
    return;
}
var connection = Environment.GetEnvironmentVariable("COACH_EVAL_CONNECTION") ?? throw new InvalidOperationException("Set COACH_EVAL_CONNECTION.");
var target = new NpgsqlConnectionStringBuilder(connection);
if (target.Host is not ("localhost" or "127.0.0.1" or "::1")) throw new InvalidOperationException("Evaluation requires a local database clone.");
var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
if (string.IsNullOrWhiteSpace(key))
{
    using var secret = JsonDocument.Parse(Environment.GetEnvironmentVariable("COACH_EVAL_SECRET_ROW") ?? throw new InvalidOperationException("Set OPENAI_API_KEY or an encrypted configuration row."));
    key = AgentPlayground.Contracts.Configuration.PostgresConfigurationCrypto.Decrypt(secret.RootElement.GetProperty("value").GetString()!,
        secret.RootElement.GetProperty("scope").GetString()!, "OpenAI:ApiKey", Environment.GetEnvironmentVariable("COACH_EVAL_CONFIG_KEY")!);
}
var models = (Environment.GetEnvironmentVariable("COACH_EVAL_MODELS") ?? "gpt-4o-mini").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (models.Length == 0 || models.Distinct(StringComparer.Ordinal).Count() != models.Length)
    throw new InvalidOperationException("Select at least one model with no duplicate IDs.");
var repeats = int.Parse(Environment.GetEnvironmentVariable("COACH_EVAL_REPEATS") ?? "3");
if (repeats is < 1 or > 10) throw new InvalidOperationException("Repeat count must be 1-10.");
var datasetBytes = await File.ReadAllBytesAsync(casePath);
var dataset = JsonSerializer.Deserialize<EvalDataset>(datasetBytes)!;
if (dataset.Version is not (1 or 2) || dataset.Cases.Length == 0 || dataset.Cases.Select(c => c.Id).Distinct().Count() != dataset.Cases.Length)
    throw new InvalidOperationException("Invalid dataset version, empty cases, or duplicate case IDs.");
var root = Path.GetFullPath(Path.Combine(".artifacts", "coach-evals", DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + Guid.NewGuid().ToString("N")[..8]));
Directory.CreateDirectory(root);
var sources = Directory.GetFiles("PersonalAgent", "*.cs", SearchOption.AllDirectories)
    .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
    .Concat(Directory.GetFiles("scripts/CoachRetrievalEvaluation", "*.cs"))
    .Append("Directory.Packages.props").Append("scripts/CoachRetrievalEvaluation/CoachRetrievalEvaluation.csproj")
    .Order().ToDictionary(p => p.Replace('\\','/'), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
await File.WriteAllBytesAsync(Path.Combine(root, "cases.json"), datasetBytes);
using (var archive = System.IO.Compression.ZipFile.Open(Path.Combine(root, "source-snapshot.zip"), System.IO.Compression.ZipArchiveMode.Create))
    foreach (var path in sources.Keys)
        System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(archive, path, path);
var inventory = await new OpenAiChatModelDiscovery(new OpenAI.OpenAIClient(key).GetOpenAIModelClient()).GetModelIdsAsync(default);
await File.WriteAllTextAsync(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(new {
    protocol = $"coach-retrieval-v{dataset.Version}", dataset.Name, datasetSha256 = Convert.ToHexString(SHA256.HashData(datasetBytes)),
    models, repeats, sources,
    binaries = new[] { typeof(AgentChatService).Assembly.Location, System.Reflection.Assembly.GetExecutingAssembly().Location }
        .ToDictionary(p => Path.GetFileName(p)!, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))), startedAtUtc = DateTimeOffset.UtcNow,
    providerAvailableModels = models.Where(inventory.Contains), endpoint = "chat/completions",
    generationSettings = "Application defaults; Luna tool requests use reasoning_effort=none for Chat Completions compatibility; no evaluation-specific override", embeddingModel = "text-embedding-3-small",
    scope = "Real chat/tool/retrieval/session path; coaching tool only; semantic recall disabled; isolated local database",
    rubric = "Deterministic concept patterns + evidence ID/timing + same-turn citation membership + tool selection. Not a semantic correctness proof."
}, json));
var results = new List<EvalResult>();
Console.WriteLine($"Evaluation evidence directory: {root}");
// Round-robin models within each repeat; every case receives a fresh session.
for (var repeat = 1; repeat <= repeats; repeat++)
foreach (var model in models)
foreach (var test in dataset.Cases)
{
    var trace = new TrialTrace();
    var timer = Stopwatch.StartNew();
    string? answer = null;
    string? failure = null;
    try
    {
        if (!inventory.Contains(model)) throw new InvalidOperationException("Model unavailable in provider inventory.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var memory = new AgentMemoryOptions { ConnectionString = connection };
        var keys = Options.Create(new ApiKeyOptions { OpenAiKey = key });
        var tavily = new Mock<ITavilyMcpToolProvider>();
        tavily.Setup(Value => Value.GetTools()).Returns(Array.Empty<AIFunction>());
        var registry = new AgentToolRegistry(tavily.Object);
        var permissions = registry.GetRegistrations().ToDictionary(Value => Value.Descriptor.Key, Value => Value.Descriptor.Key == AgentToolKeys.SearchCoachCheckins);
        var access = new Mock<IToolAccessStore>();
        access.Setup(Value => Value.GetRolePermissionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(permissions);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(memory));
        services.AddSingleton(keys);
        services.AddSingleton(Options.Create(new CoachCheckinOptions()));
        services.AddSingleton(Options.Create(new SqlTransportOptions { ConnectionString = connection }));
        services.AddSingleton(Mock.Of<IBus>());
        services.AddSingleton<IAgentEmbeddingService, OpenAiAgentEmbeddingService>();
        services.AddSingleton<CoachCheckinService>();
        services.AddSingleton<IAgentToolRegistry>(registry);
        services.AddSingleton(access.Object);
        services.AddSingleton<ToolAccessService>();
        services.AddSingleton<AgentToolBinder>();
        services.AddSingleton<IAgentSessionStore, PostgresAgentSessionStore>();
        services.AddSingleton(Mock.Of<IAgentSemanticMemoryStore>());
        services.AddSingleton<SemanticMemoryService>();
        services.AddSingleton<IAgentChatClientFactory>(new TraceFactory(keys, trace));
        var catalog = new Mock<IChatModelCatalog>();
        catalog.Setup(Value => Value.FindModelAsync(model, It.IsAny<CancellationToken>())).ReturnsAsync(new AvailableChatModel(model, model, true));
        services.AddSingleton(catalog.Object);
        services.AddSingleton<AgentChatService>();
        await using var provider = services.BuildServiceProvider();
        var chat = provider.GetRequiredService<AgentChatService>();
        var session = await chat.CreateSessionAsync(new AgentAccessContext(test.Subject, AgentRoles.Owner, test.Subject), model, timeout.Token);
        answer = await chat.SendMessageAsync(session.SessionId, new AgentAccessContext(test.Subject, AgentRoles.Owner, test.Subject), test.Question, timeout.Token);
    }
    catch (Exception error) { failure = error.GetType().Name; }
    var checks = EvalScorer.Score(test, answer, trace);
    checks["providerSucceeded"] = failure is null;
    var result = new EvalResult(model, test.Id, repeat, checks.Values.All(v => v), checks, timer.ElapsedMilliseconds,
        trace.InputTokens, trace.OutputTokens, trace.CachedTokens, trace.ReasoningTokens, failure);
    results.Add(result);
    var stem = $"{model}-{test.Id}-{repeat}";
    await File.WriteAllTextAsync(Path.Combine(root, stem + ".json"), JsonSerializer.Serialize(new { result, question = test.Question, answer, trace.Events }, json));
    Console.WriteLine($"{model} {test.Id} repeat {repeat}: {(result.Passed ? "PASS" : "FAIL")} ({timer.ElapsedMilliseconds}ms)");
    await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(results, json));
}
await EvalReports.WriteAsync(root, dataset.Name, results);
Environment.ExitCode = results.All(r => r.Passed) ? 0 : 1;

internal record EvalDataset(int Version, string Name, EvalCase[] Cases);
internal record EvalCase(string Id, string Subject, string Question, string? ExpectedUpload, int? ExpectedStartMs,
    int TimestampToleranceMs, string[] RequiredPatterns, string[] ForbiddenPatterns, string? ExpectedRecency, string? ExpectedFileName, bool NoEvidence);
internal record EvalResult(string Model, string CaseId, int Repeat, bool Passed, Dictionary<string, bool> Checks,
    long ElapsedMs, long? InputTokens, long? OutputTokens, long? CachedTokens, long? ReasoningTokens, string? ProviderFailure);
internal sealed class TrialTrace
{
    public List<object> Events { get; } = [];
    public List<FunctionCallContent> Calls { get; } = [];
    public List<string> Evidence { get; } = [];
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? CachedTokens { get; set; }
    public long? ReasoningTokens { get; set; }
}
internal sealed class TraceFactory(IOptions<ApiKeyOptions> Keys, TrialTrace Trace) : IAgentChatClientFactory
{
    public IChatClient Create(string ModelId) => new TraceClient(new OpenAiAgentChatClientFactory(Keys).Create(ModelId), Trace);
}
internal sealed class TraceClient(IChatClient Inner, TrialTrace Trace) : DelegatingChatClient(Inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> Messages, ChatOptions? Options = null, CancellationToken Token = default)
    {
        var messages = Messages.ToList();
        var evidence = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.Result?.ToString() ?? "").ToArray();
        Trace.Evidence.AddRange(evidence);
        var response = await base.GetResponseAsync(messages, Options, Token);
        var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToArray();
        Trace.Calls.AddRange(calls);
        Trace.InputTokens = Add(Trace.InputTokens, response.Usage?.InputTokenCount);
        Trace.OutputTokens = Add(Trace.OutputTokens, response.Usage?.OutputTokenCount);
        Trace.CachedTokens = Add(Trace.CachedTokens, response.Usage?.CachedInputTokenCount);
        Trace.ReasoningTokens = Add(Trace.ReasoningTokens, response.Usage?.ReasoningTokenCount);
        Trace.Events.Add(new { response.ModelId, response.ResponseId, response.Usage, evidence,
            calls = calls.Select(c => new { c.Name, c.Arguments }), rawAnswer = response.Text });
        return response;
    }
    private static long? Add(long? Total, long? Value) => Value is null ? Total : (Total ?? 0) + Value;
}
