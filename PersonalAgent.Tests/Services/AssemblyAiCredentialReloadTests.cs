using System.Net;
using AgentPlayground.Contracts.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PersonalAgent.Configuration;
using PersonalAgent.Services;
using Xunit;

namespace PersonalAgent.Tests.Services;

public sealed class AssemblyAiCredentialReloadTests
{
    [Fact]
    public async Task ExistingTranscriptionService_UsesRotatedCredentialOnItsNextRequest()
    {
        using var configuration = new ConfigurationManager();
        configuration["AssemblyAi:ApiKey"] = "original-token";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTranscription(configuration);
        var handler = new CaptureHandler();
        services.AddHttpClient("AssemblyAi").ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        var transcription = provider.GetRequiredService<ITranscriptionProvider>();
        await transcription.GetResultAsync(Guid.Empty, "synthetic-job", default);
        configuration["AssemblyAi:ApiKey"] = "replacement-token";
        ((IConfigurationRoot)configuration).Reload();
        await transcription.GetResultAsync(Guid.Empty, "synthetic-job", default);
        handler.Tokens.Should().Equal("original-token", "replacement-token");
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<string> Tokens { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken CancellationToken)
        {
            Tokens.Add(Request.Headers.GetValues("Authorization").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"status":"completed","utterances":[]}""") });
        }
    }
}
