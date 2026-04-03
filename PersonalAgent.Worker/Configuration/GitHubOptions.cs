namespace PersonalAgent.Worker.Configuration;

public record GitHubOptions
{
    public string PersonalAccessToken { get; set; } = string.Empty;
    public string RepoOwner { get; set; } = string.Empty;
    public string RepoName { get; set; } = string.Empty;
    public string Branch { get; set; } = "main";
    public string JournalPath { get; set; } = string.Empty; // e.g., "journal"
}
