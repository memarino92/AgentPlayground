using System.Text;
using PersonalAgent.Backup;
using PersonalAgent.Integrations;
using Sentry;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Xunit;

namespace PersonalAgent.Backup.Tests;

public class BackupSentryTests
{
    [Fact]
    public async Task OneShotFlushDeliversSanitizedBackupEventWithStableGrouping()
    {
        var transport = new RecordingTransport();
        var factory = new SentryErrorClientFactory(Options =>
        {
            Options.Transport = transport;
            return new SentryClient(Options);
        });
        var values = IntegrationRegistry.Merge(IntegrationRegistry.Defaults(), new()
        {
            ["Enabled"] = "true",
            ["Dsn"] = "https://0123456789abcdef0123456789abcdef@o123.ingest.us.sentry.io/456"
        });
        using var client = factory.Create(values, "Backup");
        var diagnostics = new BackupDiagnostics { ErrorClient = client };
        diagnostics.Protect("opaque-sftp-password");
        diagnostics.Begin("uploading the database dump");
        diagnostics.Context("uploaded_bytes", 2048);
        try { throw new IOException("SSH connection closed; opaque-sftp-password"); }
        catch (Exception failure) { diagnostics.Failure(failure, "Database backup failed"); }

        await client.FlushAsync(TimeSpan.FromSeconds(5));
        var envelope = await transport.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Contains("SSH connection closed", envelope);
        Assert.Contains("uploading the database dump", envelope);
        Assert.Contains(diagnostics.RunId, envelope);
        Assert.Contains("Backup", envelope);
        Assert.Contains("production", envelope);
        Assert.Contains("2048", envelope);
        Assert.Contains("OneShotFlushDelivers", envelope);
        Assert.DoesNotContain("opaque-sftp-password", envelope);
        Assert.Contains("Database backup failed", envelope);
    }

    private sealed class RecordingTransport : ITransport
    {
        public TaskCompletionSource<string> Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task SendEnvelopeAsync(Envelope Envelope, CancellationToken CancellationToken)
        {
            using var stream = new MemoryStream();
            await Envelope.SerializeAsync(stream, null, CancellationToken);
            Delivered.TrySetResult(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }
}
