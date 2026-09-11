using AgentPlayground.Contracts.Configuration;
using Microsoft.Extensions.Options;
using PersonalAgent.Services;

namespace PersonalAgent.Configuration;

internal static class TranscriptionServiceCollectionExtensions
{
    public static void AddAssemblyAiOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AssemblyAiOptions>()
            .Configure(opts =>
            {
                configuration.GetSection(AssemblyAiOptions.SectionName).Bind(opts);
                opts.ApiKey = ConfigurationValueResolver.ResolveString(configuration, "ASSEMBLYAI_API_KEY", $"{AssemblyAiOptions.SectionName}:ApiKey", opts.ApiKey)
                    ?? opts.ApiKey;
                opts.BaseUrl = ConfigurationValueResolver.ResolveString(configuration, "ASSEMBLYAI_BASE_URL", $"{AssemblyAiOptions.SectionName}:BaseUrl", opts.BaseUrl)
                    ?? opts.BaseUrl;
                opts.SpeechModels = configuration.GetSection($"{AssemblyAiOptions.SectionName}:SpeechModels").Get<List<string>>()
                    ?? opts.SpeechModels;
                opts.PollIntervalSeconds = ConfigurationValueResolver.ResolveInt(configuration, "ASSEMBLYAI_POLL_INTERVAL_SECONDS", $"{AssemblyAiOptions.SectionName}:PollIntervalSeconds", opts.PollIntervalSeconds, value => value > 0);
                opts.TranscriptionTimeoutMinutes = ConfigurationValueResolver.ResolveInt(configuration, "ASSEMBLYAI_TRANSCRIPTION_TIMEOUT_MINUTES", $"{AssemblyAiOptions.SectionName}:TranscriptionTimeoutMinutes", opts.TranscriptionTimeoutMinutes, value => value > 0);
            })
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.ApiKey), $"{AssemblyAiOptions.SectionName}:ApiKey is required")
            .Validate(opts => Uri.TryCreate(opts.BaseUrl, UriKind.Absolute, out _), $"{AssemblyAiOptions.SectionName}:BaseUrl must be an absolute URI")
            .Validate(opts => opts.SpeechModels.Count > 0 && opts.SpeechModels.All(model => !string.IsNullOrWhiteSpace(model)), $"{AssemblyAiOptions.SectionName}:SpeechModels must contain at least one model")
            .Validate(opts => opts.PollIntervalSeconds > 0, $"{AssemblyAiOptions.SectionName}:PollIntervalSeconds must be greater than zero")
            .Validate(opts => opts.TranscriptionTimeoutMinutes > 0, $"{AssemblyAiOptions.SectionName}:TranscriptionTimeoutMinutes must be greater than zero")
            .ValidateOnStart();
    }

    public static void AddTranscription(this IServiceCollection Services, IConfiguration Configuration)
    {
        Services.AddAssemblyAiOptions(Configuration);
        Services.AddLiveOptions<AssemblyAiOptions>(Configuration);
        Services.AddHttpClient("AssemblyAi", (Provider, Client) =>
        {
            var Options = Provider.GetRequiredService<IOptions<AssemblyAiOptions>>().Value;
            Client.BaseAddress = new Uri(Options.BaseUrl.TrimEnd('/') + "/");
            Client.Timeout = TimeSpan.FromMinutes(2);
            Client.DefaultRequestHeaders.Add("Authorization", Options.ApiKey);
        });
        Services.AddSingleton<ITranscriptionProvider, AssemblyAiTranscriptionService>();
        Services.AddSingleton<TranscriptionJobService>();
    }
}
