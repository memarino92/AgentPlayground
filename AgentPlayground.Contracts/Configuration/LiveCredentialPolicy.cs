namespace AgentPlayground.Contracts.Configuration;

public static class LiveCredentialPolicy
{
    public static IReadOnlyDictionary<string, string> EnvironmentVariables { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["OpenAI:ApiKey"] = "OPENAI_API_KEY",
        ["AssemblyAi:ApiKey"] = "ASSEMBLYAI_API_KEY"
    };

    public static bool Supports(string Scope, string Key) => Scope is "Shared" or "Api" && EnvironmentVariables.ContainsKey(Key);
    public static bool IsValid(string? Value) => !string.IsNullOrWhiteSpace(Value) && Value.Length <= 4096
        && Value.All(Character => Character is >= '!' and <= '~');
}
