namespace PersonalAgent.Contracts.Automations;

public sealed record AutomationRuntimeSettings
{
    public bool Enabled { get; init; }
    public string EnvironmentId { get; init; } = "";
    public string Checkpoint { get; init; } = "";
    public string ImageId { get; init; } = "";
    public string GatewayUrl { get; init; } = "";
    public string ReviewMode { get; init; } = "Off";
    public string[] ApprovedPackages { get; init; } = [];
}

public sealed record AutomationRuntimeView(long Revision, AutomationRuntimeSettings Settings, bool HasToken);
public sealed record SaveAutomationRuntime(long ExpectedRevision, AutomationRuntimeSettings Settings, string TokenAction = "keep", string? Token = null);
public sealed record AutomationOperationRequest(string OperationId, string Tool, System.Text.Json.JsonElement Inputs);
public sealed record AutomationOperationEvidence(string OperationId, string Kind, string Target, string Outcome, string Policy, DateTimeOffset CreatedAt);
public sealed record AutomationSandboxStatus(int Step, string? SandboxId, string State, DateTimeOffset ExpiresAt);
