namespace PersonalAgent.Contracts.Coding;

public static class CodingJobs
{
    public const string Permission = "Local:start_coding_job";
    public const string Queue = "personal-agent-coding-jobs";
    public const string Model = "openai/gpt-5.4-mini";
    public const int MaxSubscriptionRequests = 1000;
    public static bool IsSubscriptionModel(string? ModelId) => TrySubscriptionModel(ModelId, out _, out _);
    public static bool TrySubscriptionModel(string? ModelId, out Guid Connection, out string Slug)
    {
        var Parts = ModelId?.Split(':', 3) ?? [];
        Connection = default;
        Slug = Parts.Length == 3 ? Parts[2] : "";
        return Parts.Length == 3 && Parts[0] == "chatgpt" && Guid.TryParse(Parts[1], out Connection)
            && System.Text.RegularExpressions.Regex.IsMatch(Slug, @"\A[A-Za-z0-9_.-]{1,100}\z");
    }
    public const int MaxArtifactBytes = 1_000_000;
    public static bool Terminal(string Status) => Status is "PrOpened" or "Failed" or "Cancelled";
    public static DateTimeOffset? RulesetTimestamp(string? Value) => Value is not null
        && System.Text.RegularExpressions.Regex.IsMatch(Value, @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})\z")
        && DateTimeOffset.TryParse(Value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var Timestamp) ? Timestamp : null;
}

public sealed record ExecuteCodingJob(Guid JobId);
public sealed record StartCodingJob(string Instruction, string RequestKey);
public sealed record CodingFile(string Path, string? Content, string Mode = "100644");
public sealed record CodingValidation(string Command, int ExitCode, string Output);
public sealed record CodingArtifact(string BaseSha, CodingFile[] Files, CodingValidation[] Checks, string Summary);
public sealed record CodingJobView(Guid Id, string Instruction, string Repository, string BaseSha, string Branch,
    string Status, DateTimeOffset CreatedAt, DateTimeOffset Deadline, string? SandboxId, string CleanupState,
    int ModelRequests, string? PullRequestUrl, string? Error, CodingArtifact? Artifact);

public sealed record CodingSettings
{
    public bool Enabled { get; init; }
    public string Repository { get; init; } = "memarino92/AgentPlayground";
    public string BaseBranch { get; init; } = "main";
    public string Checkpoint { get; init; } = "";
    public string ImageId { get; init; } = "";
    public long GitHubAppId { get; init; }
    public long InstallationId { get; init; }
    public long VerifiedRulesetId { get; init; }
    public string VerifiedRulesetUpdatedAt { get; init; } = "";
    public int MaxMinutes { get; init; } = 20;
    public string ModelId { get; init; } = CodingJobs.Model;
    // OpenRouter reserves $0.40/request; subscription jobs use a separate request allowance without API dollar accounting.
    public int MaxModelRequests { get; init; } = 12;
    public decimal ModelBudgetUsd { get; init; } = 5;
    public string TestProject { get; init; } = "PersonalAgent.Api.Tests/PersonalAgent.Api.Tests.csproj";
    public string TestFilter { get; init; } = "FullyQualifiedName~AgentSkillsTests";
}

public sealed record CodingSettingsView(long Revision, CodingSettings Settings, bool HasOpenRouterKey, bool HasGitHubPrivateKey);
public sealed record CodingModelOption(string Id, string DisplayName);
public sealed record SaveCodingSettings(long ExpectedRevision, CodingSettings Settings,
    string OpenRouterKeyAction = "keep", string? OpenRouterKey = null,
    string GitHubPrivateKeyAction = "keep", string? GitHubPrivateKey = null);
