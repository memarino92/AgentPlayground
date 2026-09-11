using PersonalAgent.Configuration;
using PersonalAgent.Services;
using Microsoft.Extensions.Options;

namespace PersonalAgent.Development;

internal static class SyntheticServiceExtensions
{
    public static void AddSyntheticServices(this IServiceCollection Services)
    {
        Services.AddSingleton<IAgentChatClientFactory, SyntheticChatClient>();
        Services.AddSingleton<IAgentEmbeddingService, SyntheticEmbeddingService>();
        Services.AddSingleton<ITranscriptionProvider, SyntheticTranscriptionProvider>();
        Services.AddSingleton<IWorkJournalParsingService, SyntheticJournalParsingService>();
        Services.PostConfigure<ChatModelCatalogOptions>(Options =>
        {
            Options.Models.Clear();
            Options.Models.Add(new() { Id = "synthetic-demo", DisplayName = "Synthetic demo (fixed responses)", IsDefault = true });
        });
        Services.AddSingleton<IChatModelPolicySource, SyntheticChatModelPolicy>();
        Services.PostConfigure<ApiKeyOptions>(Options => Options.EnableWebSearch = false);
        Services.PostConfigure<PushNotificationsOptions>(Options => Options.Enabled = false);
        Services.AddHostedService<SyntheticDataInitializer>();
    }
}

internal sealed class SyntheticChatModelPolicy(IOptions<ChatModelCatalogOptions> Options) : IChatModelPolicySource
{
    public Task<ChatModelCatalogOptions> ReadAsync(CancellationToken CancellationToken) => Task.FromResult(Options.Value with { DiscoverFromProvider = false });
}
