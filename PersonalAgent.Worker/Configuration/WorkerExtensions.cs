using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgentPlayground.Contracts.Configuration;

namespace PersonalAgent.Worker.Configuration;

public static class WorkerExtensions
{
    public static WorkJournalSyncConfigurationValidation ValidateWorkJournalSyncConfiguration(IConfiguration configuration)
    {
        var missingSettings = new List<string>();

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_PAT") ?? configuration["GitHub:PersonalAccessToken"]))
            missingSettings.Add("GitHub:PersonalAccessToken");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_OWNER") ?? configuration["GitHub:RepoOwner"]))
            missingSettings.Add("GitHub:RepoOwner");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_REPO") ?? configuration["GitHub:RepoName"]))
            missingSettings.Add("GitHub:RepoName");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_JOURNAL_PATH") ?? configuration["GitHub:JournalPath"]))
            missingSettings.Add("GitHub:JournalPath");

        return new WorkJournalSyncConfigurationValidation(missingSettings.Count is 0, missingSettings);
    }

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
            });

    }

    public static void AddPersonalAgentApiOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PersonalAgentApiOptions>()
            .Configure(opts =>
            {
                configuration.GetSection(PersonalAgentApiOptions.SectionName).Bind(opts);

                opts.BaseUrl = ConfigurationValueResolver.ResolveString(
                    configuration,
                    "PERSONAL_AGENT_API_BASE_URL",
                    $"{PersonalAgentApiOptions.SectionName}:BaseUrl",
                    opts.BaseUrl)
                    ?? opts.BaseUrl;

                opts.InternalApiKey = ConfigurationValueResolver.ResolveString(
                    configuration,
                    "INTERNAL_API_KEY",
                    $"{PersonalAgentApiOptions.SectionName}:InternalApiKey",
                    opts.InternalApiKey)
                    ?? opts.InternalApiKey;
                opts.ActorSigningKey = ConfigurationValueResolver.ResolveString(
                    configuration,
                    "WEB_ACTOR_SIGNING_KEY",
                    $"{PersonalAgentApiOptions.SectionName}:ActorSigningKey",
                    opts.ActorSigningKey)
                    ?? opts.ActorSigningKey;
            })
            .Validate(opts => Uri.TryCreate(opts.BaseUrl, UriKind.Absolute, out _), $"{PersonalAgentApiOptions.SectionName}:BaseUrl must be an absolute URI")
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.InternalApiKey), $"{PersonalAgentApiOptions.SectionName}:InternalApiKey is required")
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.ActorSigningKey), $"{PersonalAgentApiOptions.SectionName}:ActorSigningKey is required")
            .ValidateOnStart();
    }

    public static void AddCoachCheckinWorkerOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CoachCheckinWorkerOptions>()
            .Configure(opts =>
            {
                configuration.GetSection(CoachCheckinWorkerOptions.SectionName).Bind(opts);
                opts.Schema = ConfigurationValueResolver.ResolveString(configuration, "COACH_CHECKINS_SCHEMA", $"{CoachCheckinWorkerOptions.SectionName}:Schema", opts.Schema)
                    ?? opts.Schema;
                opts.FailedUploadRetentionDays = ConfigurationValueResolver.ResolveInt(configuration, "COACH_CHECKINS_FAILED_UPLOAD_RETENTION_DAYS", $"{CoachCheckinWorkerOptions.SectionName}:FailedUploadRetentionDays", opts.FailedUploadRetentionDays, value => value > 0);
            })
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.Schema), $"{CoachCheckinWorkerOptions.SectionName}:Schema is required")
            .Validate(opts => opts.FailedUploadRetentionDays > 0, $"{CoachCheckinWorkerOptions.SectionName}:FailedUploadRetentionDays must be greater than zero")
            .ValidateOnStart();
    }
}

public record WorkJournalSyncConfigurationValidation(bool IsValid, List<string> MissingSettings);
