using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace PersonalAgent.Worker.Configuration;

public static class WorkerExtensions
{
    public static void AddGitHubOptions(this IServiceCollection services, IConfiguration configuration)
    {
         services.AddOptions<GitHubOptions>()
            .Configure(opts =>
            {
                opts.PersonalAccessToken = Environment.GetEnvironmentVariable("GITHUB_PAT") 
                    ?? configuration["GitHub:PersonalAccessToken"] ?? string.Empty;
                opts.RepoOwner = Environment.GetEnvironmentVariable("GITHUB_OWNER") 
                    ?? configuration["GitHub:RepoOwner"] ?? string.Empty;
                opts.RepoName = Environment.GetEnvironmentVariable("GITHUB_REPO") 
                    ?? configuration["GitHub:RepoName"] ?? string.Empty;
                opts.Branch = Environment.GetEnvironmentVariable("GITHUB_BRANCH") 
                    ?? configuration["GitHub:Branch"] ?? "main";
                opts.JournalPath = Environment.GetEnvironmentVariable("GITHUB_JOURNAL_PATH") 
                    ?? configuration["GitHub:JournalPath"] ?? string.Empty;
            })
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.PersonalAccessToken), "GitHub:PersonalAccessToken is required")
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.RepoOwner), "GitHub:RepoOwner is required")
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.RepoName), "GitHub:RepoName is required")
            .ValidateOnStart();

        services.AddOptions<ApiKeyOptions>()
            .Configure(opts =>
            {
                opts.OpenAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") 
                    ?? configuration["OpenAI:ApiKey"] ?? string.Empty;
            })
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.OpenAiKey), "OpenAI:ApiKey is required")
            .ValidateOnStart();
    }
}
