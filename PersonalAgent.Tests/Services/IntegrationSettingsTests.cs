using System.Text;
using AgentPlayground.Integrations;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Sentry;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Xunit;

namespace PersonalAgent.Tests.Services;

public sealed class IntegrationSettingsTests
{
    public const string Dsn = "https://0123456789abcdef0123456789abcdef@o123.ingest.us.sentry.io/456";

    [Theory]
    [InlineData("http://0123456789abcdef0123456789abcdef@o123.ingest.us.sentry.io/456")]
    [InlineData("https://0123456789abcdef0123456789abcdef@localhost/456")]
    [InlineData("https://0123456789abcdef0123456789abcdef@o123.ingest.us.sentry.io.evil.com/456")]
    [InlineData("https://0123456789abcdef0123456789abcdef@o123.ingest.us.sentry.io:444/456")]
    [InlineData("https://0123456789abcdef0123456789abcdef@o123.ingest.us.sentry.io/456?secret=x")]
    [InlineData("https://0123456789abcdef0123456789abcdef:password@o123.ingest.us.sentry.io/456")]
    [InlineData("https://0123456789abcdef0123456789abcdef@127.0.0.1/456")]
    public void InvalidOrUnsafeDsn_IsRejected(string Value) => IntegrationRegistry.IsAllowedDsn(Value).Should().BeFalse();

    [Fact]
    public void KeepReplaceClear_AndInvalidCombinations()
    {
        var saved = Values();
        IntegrationRegistry.Merge(saved, new() { ["Environment"] = "staging" })["Dsn"].Should().Be(Dsn);
        var cleared = IntegrationRegistry.Merge(saved, new() { ["Enabled"] = "false", ["Dsn"] = "" });
        cleared["Dsn"].Should().BeEmpty();
        ((Action)(() => IntegrationRegistry.Merge(saved, new() { ["Dsn"] = "" }))).Should().Throw<IntegrationValidationException>();
        ((Action)(() => IntegrationRegistry.Merge(saved, new() { ["SampleRate"] = "NaN" }))).Should().Throw<IntegrationValidationException>();
        ((Action)(() => IntegrationRegistry.Merge(saved, new() { ["ArbitrarySecret"] = "x" }))).Should().Throw<IntegrationValidationException>();
    }

    [Fact]
    public void Overrides_AreExplicit_AndDoNotMutateSavedValues()
    {
        var saved = Values();
        var (effective, overrides) = IntegrationRegistry.ResolveOverrides(saved, Key => Key == "SENTRY_ENABLED" ? "false" : null);
        effective["Enabled"].Should().Be("false");
        saved["Enabled"].Should().Be("true");
        overrides.Should().Equal("SENTRY_ENABLED");
    }

    [Fact]
    public async Task Reload_ReplacesOnce_Disables_Reenables_AndRetainsClientOnFailure()
    {
        var store = new MemoryStore { Revision = new(1, 1, Values()) };
        var factory = new FakeFactory();
        using var runtime = new IntegrationRuntime(store, factory, new("Api"));
        await runtime.ReloadAsync(default);
        await runtime.ReloadAsync(default);
        factory.Clients.Should().ContainSingle();
        runtime.CaptureTest(1).Should().Be("event");
        store.Revision = new(2, 2, IntegrationRegistry.Merge(Values(), new() { ["Environment"] = "staging" }));
        factory.Fail = true;
        await runtime.ReloadAsync(default);
        store.Acknowledgements.Last().Revision.Should().Be(1);
        store.Acknowledgements.Last().Status.Should().Contain("failed");
        runtime.CaptureTest(1).Should().Be("event");
        ((Action)(() => runtime.CaptureTest(2))).Should().Throw<IntegrationConflictException>();
        factory.Fail = false;
        await runtime.ReloadAsync(default);
        factory.Clients[0].Disposed.Should().BeTrue();
        factory.Clients.Should().HaveCount(2);
        store.Revision = new(3, 3, IntegrationRegistry.Merge(Values(), new() { ["Enabled"] = "false" }));
        await runtime.ReloadAsync(default);
        runtime.CaptureTest(3).Should().BeNull();
        factory.Clients[1].Disposed.Should().BeTrue();
        store.Revision = new(4, 4, Values());
        await runtime.ReloadAsync(default);
        runtime.CaptureTest(4).Should().Be("event");
        factory.Clients.Should().HaveCount(3);
    }

    [Fact]
    public async Task DatabaseAndExporterFailures_RetainClient_AndDoNotFailLoggingOrShutdown()
    {
        var store = new MemoryStore { Revision = new(1, 1, Values()) };
        var factory = new FakeFactory();
        using var runtime = new IntegrationRuntime(store, factory, new("Api"));
        await runtime.ReloadAsync(default);
        store.ReadFailure = true;
        await ((Func<Task>)(() => runtime.ReloadAsync(default))).Should().ThrowAsync<InvalidOperationException>();
        runtime.CaptureTest(1).Should().Be("event");
        factory.Clients.Single().ThrowOnCapture = true;
        runtime.CaptureTest(1).Should().BeNull();
        factory.Clients.Single().ThrowOnDispose = true;
        ((Action)runtime.Dispose).Should().NotThrow();
    }

    [Fact]
    public async Task Logger_DoesNotFormatSensitiveState_AndIgnoresSdkLogs()
    {
        var store = new MemoryStore { Revision = new(1, 1, Values()) };
        var factory = new FakeFactory();
        using var runtime = new IntegrationRuntime(store, factory, new("Api"));
        await runtime.ReloadAsync(default);
        using var provider = new IntegrationLoggingProvider(runtime);
        var logger = provider.CreateLogger("Application.Example");
        logger.Log(LogLevel.Error, new EventId(42), "private journal", new InvalidOperationException("secret token"),
            (_, _) => throw new Exception("Formatting private state is forbidden"));
        provider.CreateLogger("Sentry.Internal").LogError("Ignore recursive SDK errors");
        var error = factory.Clients.Single().Errors.Should().ContainSingle().Subject;
        error.ExceptionType.Should().Be(typeof(InvalidOperationException).FullName);
        error.EventId.Should().Be(42);
    }

    [Fact]
    public async Task RealSdk_QueuesSanitizedEnvelope_AndFlushesOnReplacement()
    {
        var transport = new RecordingTransport();
        var factory = new SentryErrorClientFactory(Options =>
        {
            Options.Transport = transport;
            return new SentryClient(Options);
        });
        var store = new MemoryStore { Revision = new(1, 1, Values()) };
        using var runtime = new IntegrationRuntime(store, factory, new("Api"));
        await runtime.ReloadAsync(default);
        using var provider = new IntegrationLoggingProvider(runtime);
        provider.CreateLogger("Application.Example").LogError(new Exception("private transcript secret"), "Credential {Key}", "private-key");
        store.Revision = new(2, 2, IntegrationRegistry.Merge(Values(), new() { ["Environment"] = "staging" }));
        await runtime.ReloadAsync(default);
        var envelope = await transport.Envelope.Task.WaitAsync(TimeSpan.FromSeconds(5));
        envelope.Should().Contain("Application error (content redacted)").And.Contain("exception_type").And.Contain("production");
        envelope.Should().NotContain("private transcript").And.NotContain("private-key");
        runtime.CaptureTest(2).Should().NotBeNullOrEmpty();
    }

    public static Dictionary<string, string> Values() => IntegrationRegistry.Merge(IntegrationRegistry.Defaults(), new() { ["Enabled"] = "true", ["Dsn"] = Dsn });

    public sealed class FakeFactory : IIntegrationErrorClientFactory
    {
        public List<FakeClient> Clients { get; } = [];
        public bool Fail { get; set; }
        public IIntegrationErrorClient Create(IReadOnlyDictionary<string, string> Values, string Service)
        {
            if (Fail) throw new InvalidOperationException("SDK initialization failed");
            var client = new FakeClient();
            Clients.Add(client);
            return client;
        }
    }

    public sealed class FakeClient : IIntegrationErrorClient
    {
        public bool Disposed { get; private set; }
        public bool ThrowOnCapture { get; set; }
        public bool ThrowOnDispose { get; set; }
        public List<IntegrationError> Errors { get; } = [];
        public string Capture(IntegrationError Error)
        {
            if (ThrowOnCapture) throw new InvalidOperationException("Exporter unavailable");
            Errors.Add(Error);
            return "event";
        }
        public void Dispose()
        {
            Disposed = true;
            if (ThrowOnDispose) throw new InvalidOperationException("Exporter shutdown failed");
        }
    }

    public sealed class MemoryStore : IIntegrationSettingsStore
    {
        public IntegrationRevision Revision { get; set; } = new(0, 0, IntegrationRegistry.Defaults());
        public bool ReadFailure { get; set; }
        public List<IntegrationInstanceResponse> Acknowledgements { get; } = [];
        public Task<IntegrationRevision> ReadAsync(bool Active, CancellationToken CancellationToken) => ReadFailure
            ? throw new InvalidOperationException("Database unavailable") : Task.FromResult(Revision);
        public Task AcknowledgeAsync(IntegrationInstanceResponse Instance, CancellationToken CancellationToken) { Acknowledgements.Add(Instance); return Task.CompletedTask; }
        public Task<IReadOnlyList<IntegrationInstanceResponse>> InstancesAsync(CancellationToken CancellationToken) => Task.FromResult<IReadOnlyList<IntegrationInstanceResponse>>(Acknowledgements);
        public Task<long> SaveAsync(SaveIntegrationRequest Request, string Actor, CancellationToken CancellationToken) => throw new NotSupportedException();
        public Task ApplyAsync(long Revision, string Actor, CancellationToken CancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingTransport : ITransport
    {
        public TaskCompletionSource<string> Envelope { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task SendEnvelopeAsync(Envelope Envelope, CancellationToken CancellationToken)
        {
            using var stream = new MemoryStream();
            await Envelope.SerializeAsync(stream, null, CancellationToken);
            this.Envelope.TrySetResult(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }
}
