using AgentPlayground.Contracts.Messaging.Responses;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using PersonalAgent.Configuration;
using PersonalAgent.Services;
using Xunit;

namespace PersonalAgent.Tests.Services;

public class TranscriptionJobServiceTests(PostgresVectorFixture Database) : IClassFixture<PostgresVectorFixture>
{
    [Fact]
    public async Task RestartAndReplay_ReuseProviderJob_AndCacheCompletion()
    {
        var (Options, Id) = await SetupAsync();
        var Provider = new FakeProvider();
        (await Create(Options, Provider).GetAsync(Id, "owner", default)).Status.Should().Be(TranscriptionStatus.Pending);
        Provider.Complete = true;
        var Result = await Create(Options, Provider).GetAsync(Id, "owner", default);
        Result.Status.Should().Be(TranscriptionStatus.Completed);
        Result.Segments.Should().ContainSingle().Which.Text.Should().Be("Synthetic transcript");
        var Replacement = new FakeProvider();
        (await Create(Options, Replacement).GetAsync(Id, "owner", default)).Should().BeEquivalentTo(Result);
        Provider.Submissions.Should().Be(1);
        Replacement.Submissions.Should().Be(0);
        Replacement.Polls.Should().Be(0);
    }

    [Fact]
    public async Task WrongSubject_CannotSubmitOrReadJob()
    {
        var (Options, Id) = await SetupAsync();
        var Provider = new FakeProvider();
        var Result = await Create(Options, Provider).GetAsync(Id, "other", default);
        Result.Status.Should().Be(TranscriptionStatus.Failed);
        Provider.Submissions.Should().Be(0);
        Provider.Polls.Should().Be(0);
    }

    [Fact]
    public async Task AmbiguousSubmission_IsNeverAutomaticallyRepeated()
    {
        var (Options, Id) = await SetupAsync();
        var Provider = new FakeProvider { FailSubmission = true };
        var First = await Create(Options, Provider).GetAsync(Id, "owner", default);
        First.Status.Should().Be(TranscriptionStatus.Failed);
        First.Error.Should().Contain("operator review");
        (await Create(Options, Provider).GetAsync(Id, "owner", default)).Should().BeEquivalentTo(First);
        Provider.Submissions.Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentRequests_SubmitOnlyOnce()
    {
        var (Options, Id) = await SetupAsync();
        var Provider = new FakeProvider();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Create(Options, Provider).GetAsync(Id, "owner", default)));
        Provider.Submissions.Should().Be(1);
    }

    [Fact]
    public async Task Deadline_SurvivesRestart_AndStopsPolling()
    {
        var (Options, Id) = await SetupAsync();
        var Provider = new FakeProvider();
        await Create(Options, Provider).GetAsync(Id, "owner", default);
        await using var Connection = new NpgsqlConnection(Database.ConnectionString);
        await Connection.OpenAsync();
        await using var Command = new NpgsqlCommand($"UPDATE {Options.Schema}.transcription_jobs SET deadline = now() - interval '1 minute'", Connection);
        await Command.ExecuteNonQueryAsync();
        var Result = await Create(Options, Provider).GetAsync(Id, "owner", default);
        Result.Error.Should().Be("Transcription timed out.");
        Provider.Polls.Should().Be(1);
    }

    private async Task<(AgentMemoryOptions, Guid)> SetupAsync()
    {
        var Schema = "transcription_" + Guid.NewGuid().ToString("N");
        var Options = new AgentMemoryOptions { ConnectionString = Database.ConnectionString, Schema = Schema, CreateInfrastructure = true };
        await new AgentMemorySchemaInitializer(Microsoft.Extensions.Options.Options.Create(Options), NullLogger<AgentMemorySchemaInitializer>.Instance).StartAsync(default);
        var Id = Guid.NewGuid();
        await using var Connection = new NpgsqlConnection(Database.ConnectionString);
        await Connection.OpenAsync();
        await using var Command = new NpgsqlCommand($"""
            INSERT INTO {Schema}.coach_call_uploads
            (upload_id, profile_id, session_id, correlation_id, original_file_name, mime_type, size_bytes, file_hash, audio_bytes, status, created_at, updated_at)
            VALUES (@id, 'owner', @id, @id, 'synthetic.wav', 'audio/wav', 3, 'synthetic', @audio, 'Uploaded', now(), now())
            """, Connection);
        Command.Parameters.AddWithValue("id", Id);
        Command.Parameters.AddWithValue("audio", new byte[] { 1, 2, 3 });
        await Command.ExecuteNonQueryAsync();
        return (Options, Id);
    }

    private static TranscriptionJobService Create(AgentMemoryOptions Options, ITranscriptionProvider Provider) =>
        new(Microsoft.Extensions.Options.Options.Create(Options), Microsoft.Extensions.Options.Options.Create(new AssemblyAiOptions()), Provider, TimeProvider.System, NullLogger<TranscriptionJobService>.Instance);

    private sealed class FakeProvider : ITranscriptionProvider
    {
        public int Submissions;
        public int Polls;
        public bool Complete;
        public bool FailSubmission;
        public Task<string> SubmitAsync(byte[] Audio, string MimeType, CancellationToken CancellationToken)
        {
            Interlocked.Increment(ref Submissions);
            if (FailSubmission) throw new HttpRequestException("Private vendor payload");
            return Task.FromResult("provider-job");
        }
        public Task<TranscriptionResponse> GetResultAsync(Guid JobId, string ProviderJobId, CancellationToken CancellationToken)
        {
            ProviderJobId.Should().Be("provider-job");
            Interlocked.Increment(ref Polls);
            return Task.FromResult(Complete
                ? new TranscriptionResponse(JobId, TranscriptionStatus.Completed, [new(0, 0, 100, "Synthetic transcript", 0.9)])
                : new TranscriptionResponse(JobId, TranscriptionStatus.Pending, []));
        }
    }
}
