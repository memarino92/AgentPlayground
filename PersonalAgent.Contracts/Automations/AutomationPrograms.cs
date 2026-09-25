namespace PersonalAgent.Contracts.Automations;

// The API authorizes and expands input before dispatch. Programs never receive actor credentials.
public sealed record ExecuteAutomationProgram(Guid RunId, int StepIndex, string Source, string Input, DateTimeOffset ExpiresAt,
    string[]? Packages = null, string? PackageLock = null);
public sealed record AutomationProgramEvidence(string SourceHash, string ImageId, int? ExitCode,
    string Status, string StandardError, double DurationSeconds, string StandardOutput = "", string? SandboxId = null);

public static class AutomationPrograms
{
    public const string PermissionKey = "Local:csharp_automation";
    public const string Queue = "personal-agent-automation-programs";
    public const string SandboxImage = "agentplayground-csharp-sandbox:1";
    public const int MaxSource = 24000;
    public const int MaxInput = 65536;
    public const int MaxOutput = 32768;
    public const int DeadlineSeconds = 90;
}
