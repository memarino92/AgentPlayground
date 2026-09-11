using AgentPlayground.Contracts.Commands;
using AgentPlayground.Contracts.Messaging;
using AgentPlayground.Contracts.Messaging.Requests;
using AgentPlayground.Contracts.Messaging.Responses;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using PersonalAgent.Configuration;
using PersonalAgent.Services;
using PersonalAgent.Worker.Configuration;
using PersonalAgent.Worker.Consumers;
using PersonalAgent.Worker.Models;
using PersonalAgent.Worker.Services;
using Xunit;

namespace PersonalAgent.Worker.Tests.Consumers;

public class CoachCallRecoveryTests(WorkerPostgresVectorFixture Database) : IClassFixture<WorkerPostgresVectorFixture>
{
    [Fact]
    public async Task RestartedTransport_DeliversPersistedCommandToProcessingQueue()
    {
        var State = await SetupAsync();
        State.Provider.Complete = true;
        await Transcribe(State); // No bus or dispatcher exists when completion commits.
        var Received = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        IHost CreateHost(bool Receive) => new HostBuilder().ConfigureServices(Services =>
        {
            Services.AddLogging();
            Services.Configure<SqlTransportOptions>(Options =>
            {
                Options.ConnectionString = Database.ConnectionString;
                Options.Schema = State.Schema + "_bus";
            });
            Services.Configure<MassTransitHostOptions>(Options => Options.WaitUntilStarted = true);
            Services.Configure<CoachCheckinWorkerOptions>(Options => Options.Schema = State.Schema);
            if (Receive) Services.AddHostedService<CoachCallOutboxDispatcher>();
            Services.AddPostgresMigrationHostedService(Options =>
            {
                Options.CreateDatabase = false;
                Options.CreateSchema = true;
                Options.CreateInfrastructure = true;
            });
            Services.AddMassTransit(Registration => Registration.UsingPostgres((Context, Config) =>
            {
                Config.UsePostgres(Context, Host =>
                {
                    Host.ConnectionString = Database.ConnectionString;
                    Host.Schema = State.Schema + "_bus";
                });
                if (Receive) Config.ReceiveEndpoint(MessagingEndpointNames.CoachCallProcessing, Endpoint =>
                    Endpoint.Handler<ProcessCoachTranscriptCommand>(async Context =>
                    {
                        await Process(State, Context.Message);
                        Received.TrySetResult(Context.MessageId!.Value);
                    }));
            }));
        }).Build();
        Guid SentId = default;
        using (var FirstHost = CreateHost(false))
        {
            await FirstHost.StartAsync();
            var Bus = FirstHost.Services.GetRequiredService<IBus>();
            while (await CoachCallOutbox.DispatchOneAsync(Database.ConnectionString, State.Schema, async (Id, Message, Token) =>
            {
                if (Message is ProcessCoachTranscriptCommand) SentId = Id;
                await CoachCallOutbox.DeliverAsync(Bus, Id, Message, Token);
            }, default)) { }
            await FirstHost.StopAsync();
        }
        using var RestartedHost = CreateHost(true);
        await RestartedHost.StartAsync();
        try
        {
            (await Received.Task.WaitAsync(TimeSpan.FromSeconds(15))).Should().Be(SentId);
            (await Scalar(State, "SELECT status FROM {0}.coach_call_uploads")).Should().Be("Completed");
            (await Scalar(State, "SELECT encode(audio_bytes, 'hex') FROM {0}.coach_call_uploads")).Should().Be("010203");
            using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while ((long)(await Scalar(State, "SELECT count(*) FROM {0}.coach_call_outbox"))! != 0)
                await Task.Delay(50, Timeout.Token);
        }
        finally { await RestartedHost.StopAsync(); }
    }

    [Fact]
    public async Task CancellationBeforeCompletion_DoesNotFailUpload_AndRestartResumes()
    {
        var State = await SetupAsync();
        var Transcription = new Mock<ITranscriptionService>();
        Transcription.Setup(Service => Service.TranscribeAsync(State.Id, "owner", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        await Assert.ThrowsAsync<OperationCanceledException>(() => new TranscribeCoachCallConsumer(State.Sql, State.Worker, Transcription.Object, NullLogger<TranscribeCoachCallConsumer>.Instance)
            .Consume(Context(new TranscribeCoachCallCommand(State.Id, "owner", State.Id))));
        (await Scalar(State, "SELECT status FROM {0}.coach_call_uploads")).Should().Be("Transcribing");
        (await Scalar(State, "SELECT count(*) FROM {0}.coach_call_outbox")).Should().Be(0L);
        State.Provider.Complete = true;
        await Transcribe(State);
        (await Scalar(State, "SELECT status FROM {0}.coach_call_uploads")).Should().Be("Processing");
    }

    [Fact]
    public async Task TerminalFailure_StatusAndNotificationAreAtomic_AndReplayIsIgnored()
    {
        var State = await SetupAsync();
        var Transcription = new Mock<ITranscriptionService>();
        Transcription.Setup(Service => Service.TranscribeAsync(State.Id, "owner", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TranscriptionFailedException("Transcription timed out."));
        Task Fail() => new TranscribeCoachCallConsumer(State.Sql, State.Worker, Transcription.Object, NullLogger<TranscribeCoachCallConsumer>.Instance)
            .Consume(Context(new TranscribeCoachCallCommand(State.Id, "owner", State.Id)));
        await FailOutboxAsync(State, true);
        await Assert.ThrowsAsync<PostgresException>(Fail);
        (await Scalar(State, "SELECT status FROM {0}.coach_call_uploads")).Should().Be("Transcribing");
        await FailOutboxAsync(State, false);
        await Fail();
        await Fail();
        (await Scalar(State, "SELECT status FROM {0}.coach_call_uploads")).Should().Be("Failed");
        (await Scalar(State, "SELECT count(*) FROM {0}.coach_call_outbox WHERE message_type = 'CoachCallProcessingFailedEvent'")).Should().Be(1L);
    }

    [Fact]
    public async Task TranscriptionRollback_RestartAndLostAcknowledgement_ResumeWithoutDuplicateWork()
    {
        var State = await SetupAsync();
        // Submission survives replacement of both API job service and Worker consumer.
        (await State.Gateway().GetAsync(State.Id, "owner", default)).Status.Should().Be(TranscriptionStatus.Pending);
        State.Provider.Complete = true;
        await FailOutboxAsync(State, true);
        await Assert.ThrowsAsync<PostgresException>(() => Transcribe(State));
        (await Scalar(State, "SELECT status FROM {0}.coach_call_uploads")).Should().Be("Transcribing");
        (await Scalar(State, "SELECT count(*) FROM {0}.coach_call_utterances")).Should().Be(0L);
        (await Scalar(State, "SELECT transcript_text FROM {0}.coach_call_sessions")).Should().Be("");
        (await Scalar(State, "SELECT count(*) FROM {0}.coach_call_outbox")).Should().Be(0L);
        await FailOutboxAsync(State, false);
        await Transcribe(State);
        var UtteranceId = await Scalar(State, "SELECT utterance_id FROM {0}.coach_call_utterances");
        await Transcribe(State);
        (await Scalar(State, "SELECT utterance_id FROM {0}.coach_call_utterances")).Should().Be(UtteranceId);
        State.Provider.Submissions.Should().Be(1);

        var Deliveries = new List<Guid>();
        var LostAcknowledgement = false;
        for (var Attempt = 0; Attempt < 3 && !LostAcknowledgement; Attempt++)
        {
            try
            {
                await CoachCallOutbox.DispatchOneAsync(Database.ConnectionString, State.Schema, async (Id, Message, Token) =>
                {
                    if (Message is not ProcessCoachTranscriptCommand Command) return;
                    Deliveries.Add(Id);
                    await Process(State, Command);
                    LostAcknowledgement = true;
                    throw new IOException("Simulated process loss after transport acceptance");
                }, default);
            }
            catch (IOException) { }
        }
        LostAcknowledgement.Should().BeTrue();
        var ChunkId = await Scalar(State, "SELECT chunk_id FROM {0}.coach_call_chunks");
        var UpdatedAt = await Scalar(State, "SELECT updated_at FROM {0}.coach_call_sessions");
        while (await CoachCallOutbox.DispatchOneAsync(Database.ConnectionString, State.Schema, async (Id, Message, Token) =>
        {
            if (Message is not ProcessCoachTranscriptCommand Command) return;
            Deliveries.Add(Id);
            await Process(State, Command);
        }, default)) { }
        Deliveries.Should().HaveCount(2);
        Deliveries[1].Should().Be(Deliveries[0]);
        (await Scalar(State, "SELECT chunk_id FROM {0}.coach_call_chunks")).Should().Be(ChunkId);
        (await Scalar(State, "SELECT updated_at FROM {0}.coach_call_sessions")).Should().Be(UpdatedAt);
        (await Scalar(State, "SELECT status FROM {0}.coach_call_uploads")).Should().Be("Completed");
        (await Scalar(State, "SELECT encode(audio_bytes, 'hex') FROM {0}.coach_call_uploads")).Should().Be("010203");
        State.EmbeddingCalls.Should().Be(1);
        State.Provider.Submissions.Should().Be(1);
    }

    [Fact]
    public async Task FinalWriteRollback_LeavesAudioAndProcessing_RedeliveryCommitsOnce()
    {
        var State = await SetupAsync();
        State.Provider.Complete = true;
        await Transcribe(State);
        await FailOutboxAsync(State, true);
        await Assert.ThrowsAsync<PostgresException>(() => Process(State, State.Command));
        (await Scalar(State, "SELECT status FROM {0}.coach_call_uploads")).Should().Be("Processing");
        (await Scalar(State, "SELECT audio_bytes IS NOT NULL FROM {0}.coach_call_uploads")).Should().Be(true);
        (await Scalar(State, "SELECT count(*) FROM {0}.coach_call_chunks")).Should().Be(0L);
        (await Scalar(State, "SELECT summary_markdown FROM {0}.coach_call_sessions")).Should().Be("");
        await FailOutboxAsync(State, false);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Process(State, State.Command)));
        (await Scalar(State, "SELECT count(*) FROM {0}.coach_call_chunks")).Should().Be(1L);
        (await Scalar(State, "SELECT count(*) FROM {0}.coach_call_outbox WHERE message_type = 'CoachCallProcessingCompletedEvent'")).Should().Be(1L);
        State.EmbeddingCalls.Should().Be(2); // Failed transaction plus one committed attempt.
        (await Scalar(State, "SELECT encode(audio_bytes, 'hex') FROM {0}.coach_call_uploads")).Should().Be("010203");
    }

    [Theory]
    [InlineData("Completed", 30, true)]
    [InlineData("Processing", 30, true)]
    [InlineData("AwaitingSpeakerOverride", 30, true)]
    [InlineData("Failed", 30, false)]
    [InlineData("Failed", 1, true)]
    public async Task Cleanup_ExpiresOnlyOldFailedAudio(string Status, int AgeDays, bool Retained)
    {
        var State = await SetupAsync();
        await Execute(State, $"UPDATE {State.Schema}.coach_call_uploads SET status = '{Status}', updated_at = now() - interval '{AgeDays} days'");
        var Cleanup = new CoachCallCleanupService(State.Sql, State.Worker, NullLogger<CoachCallCleanupService>.Instance);
        await Cleanup.CleanupAsync(default);
        await Cleanup.CleanupAsync(default);
        (await Scalar(State, "SELECT COALESCE(encode(audio_bytes, 'hex'), 'unavailable') FROM {0}.coach_call_uploads"))
            .Should().Be(Retained ? "010203" : "unavailable");
        (await Scalar(State, "SELECT count(*) FROM {0}.coach_call_sessions")).Should().Be(1L);
    }

    [Fact]
    public async Task LegacyMissingAudio_ProcessingDoesNotRecreateRecording()
    {
        var State = await SetupAsync();
        State.Provider.Complete = true;
        await Transcribe(State);
        await Execute(State, $"UPDATE {State.Schema}.coach_call_uploads SET audio_bytes = NULL");
        await Process(State, State.Command);
        await Process(State, State.Command);
        (await Scalar(State, "SELECT status FROM {0}.coach_call_uploads")).Should().Be("Completed");
        (await Scalar(State, "SELECT audio_bytes IS NULL FROM {0}.coach_call_uploads")).Should().Be(true);
        (await Scalar(State, "SELECT count(*) FROM {0}.coach_call_chunks")).Should().Be(1L);
    }

    [Fact]
    public async Task ConcurrentTranscriptionRedelivery_OneTranscriptAndContinuation()
    {
        var State = await SetupAsync();
        State.Provider.Complete = true;
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Transcribe(State)));
        (await Scalar(State, "SELECT count(*) FROM {0}.coach_call_utterances")).Should().Be(1L);
        (await Scalar(State, "SELECT count(*) FROM {0}.coach_call_outbox")).Should().Be(2L);
        State.Provider.Submissions.Should().Be(1);
        await Process(State, State.Command with { ProfileId = "other" });
        await Process(State, State.Command with { SessionId = Guid.NewGuid() });
        State.EmbeddingCalls.Should().Be(0);
        (await Scalar(State, "SELECT status FROM {0}.coach_call_uploads")).Should().Be("Processing");
    }

    [Fact]
    public async Task SpeakerReview_RollbackAndReplay_OnlyOneContinuation()
    {
        var State = await SetupAsync();
        State.Provider.Complete = true;
        State.Provider.MultipleSpeakers = true;
        await Transcribe(State);
        (await Scalar(State, "SELECT status FROM {0}.coach_call_uploads")).Should().Be("AwaitingSpeakerOverride");
        (await Scalar(State, "SELECT count(*) FROM {0}.coach_call_outbox WHERE message_type = 'ProcessCoachTranscriptCommand'")).Should().Be(0L);
        CoachCheckinService Review() => new(Mock.Of<IBus>(), State.Sql, Options.Create(State.Memory), Options.Create(new CoachCheckinOptions()), Mock.Of<IAgentEmbeddingService>(), NullLogger<CoachCheckinService>.Instance);
        await FailOutboxAsync(State, true);
        await Assert.ThrowsAsync<PostgresException>(() => Review().ApplySpeakerOverridesAsync(State.Id, "owner", [new(0, "coach")]));
        (await Scalar(State, "SELECT status FROM {0}.coach_call_uploads")).Should().Be("AwaitingSpeakerOverride");
        (await Scalar(State, "SELECT count(*) FROM {0}.coach_call_speaker_overrides")).Should().Be(0L);
        await FailOutboxAsync(State, false);
        await Review().ApplySpeakerOverridesAsync(State.Id, "owner", [new(0, "coach")]);
        await Review().ApplySpeakerOverridesAsync(State.Id, "owner", [new(0, "athlete")]);
        (await Scalar(State, "SELECT speaker_role FROM {0}.coach_call_speaker_overrides")).Should().Be("coach");
        (await Scalar(State, "SELECT count(*) FROM {0}.coach_call_outbox WHERE message_type = 'ProcessCoachTranscriptCommand'")).Should().Be(1L);
    }

    private async Task<State> SetupAsync()
    {
        var State = new State(Database.ConnectionString);
        await new AgentMemorySchemaInitializer(Options.Create(State.Memory), NullLogger<AgentMemorySchemaInitializer>.Instance).StartAsync(default);
        await Execute(State, $"""
            INSERT INTO {State.Schema}.coach_call_uploads
            (upload_id, profile_id, session_id, correlation_id, original_file_name, mime_type, size_bytes, file_hash, audio_bytes, status, created_at, updated_at)
            VALUES ('{State.Id}', 'owner', '{State.Id}', '{State.Id}', 'synthetic.wav', 'audio/wav', 3, 'synthetic', decode('010203','hex'), 'Uploaded', now(), now());
            INSERT INTO {State.Schema}.coach_call_sessions (session_id, upload_id, profile_id, created_at, updated_at)
            VALUES ('{State.Id}', '{State.Id}', 'owner', now(), now());
            """);
        return State;
    }

    private static Task Transcribe(State State) => new TranscribeCoachCallConsumer(State.Sql, State.Worker, new GatewayAdapter(State), NullLogger<TranscribeCoachCallConsumer>.Instance)
        .Consume(Context(new TranscribeCoachCallCommand(State.Id, "owner", State.Id)));

    private static Task Process(State State, ProcessCoachTranscriptCommand Command)
    {
        var Client = new Mock<IRequestClient<GenerateEmbeddingsRequest>>();
        var Response = new Mock<Response<GenerateEmbeddingsResponse>>();
        Response.SetupGet(Value => Value.Message).Returns(new GenerateEmbeddingsResponse([[0.1f, 0.2f, 0.3f]], 3));
        Client.Setup(Value => Value.GetResponse<GenerateEmbeddingsResponse>(It.IsAny<GenerateEmbeddingsRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref State.EmbeddingCalls)).ReturnsAsync(Response.Object);
        return new ProcessCoachTranscriptConsumer(State.Sql, State.Worker, new CoachTranscriptProcessingService(Client.Object), NullLogger<ProcessCoachTranscriptConsumer>.Instance).Consume(Context(Command));
    }

    private static ConsumeContext<T> Context<T>(T Message) where T : class
    {
        var Context = new Mock<ConsumeContext<T>>();
        Context.SetupGet(Value => Value.Message).Returns(Message);
        return Context.Object;
    }

    private Task FailOutboxAsync(State State, bool Fail) => Execute(State, Fail
        ? $"ALTER TABLE {State.Schema}.coach_call_outbox ADD CONSTRAINT inject_failure CHECK (false) NOT VALID"
        : $"ALTER TABLE {State.Schema}.coach_call_outbox DROP CONSTRAINT inject_failure");

    private async Task Execute(State State, string Sql)
    {
        await using var Connection = new NpgsqlConnection(Database.ConnectionString);
        await Connection.OpenAsync();
        await using var Command = new NpgsqlCommand(Sql, Connection);
        await Command.ExecuteNonQueryAsync();
    }

    private async Task<object?> Scalar(State State, string Sql)
    {
        await using var Connection = new NpgsqlConnection(Database.ConnectionString);
        await Connection.OpenAsync();
        await using var Command = new NpgsqlCommand(string.Format(Sql, State.Schema), Connection);
        return await Command.ExecuteScalarAsync();
    }

    private sealed class State(string ConnectionString)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Schema { get; } = "recovery_" + Guid.NewGuid().ToString("N");
        public Provider Provider { get; } = new();
        public int EmbeddingCalls;
        public AgentMemoryOptions Memory => new() { ConnectionString = ConnectionString, Schema = Schema, CreateInfrastructure = true, VectorDimensions = 3 };
        public IOptions<SqlTransportOptions> Sql => Options.Create(new SqlTransportOptions { ConnectionString = ConnectionString });
        public IOptions<CoachCheckinWorkerOptions> Worker => Options.Create(new CoachCheckinWorkerOptions { Schema = Schema });
        public ProcessCoachTranscriptCommand Command => new(Id, Id, "owner", Id);
        public TranscriptionJobService Gateway() => new(Options.Create(Memory), Options.Create(new AssemblyAiOptions()), Provider, TimeProvider.System, NullLogger<TranscriptionJobService>.Instance);
    }

    private sealed class GatewayAdapter(State State) : ITranscriptionService
    {
        public async Task<List<TranscribedUtterance>> TranscribeAsync(Guid UploadId, string ProfileId, CancellationToken CancellationToken)
        {
            TranscriptionResponse Result;
            do { Result = await State.Gateway().GetAsync(UploadId, ProfileId, CancellationToken); }
            while (Result.Status == TranscriptionStatus.Pending);
            return Result.Segments.Select(Segment => new TranscribedUtterance(Segment.SpeakerLabel, "coach", Segment.StartMs, Segment.EndMs, Segment.Text, Segment.Confidence)).ToList();
        }
    }

    private sealed class Provider : ITranscriptionProvider
    {
        public int Submissions;
        public bool Complete;
        public bool MultipleSpeakers;
        public Task<string> SubmitAsync(byte[] Audio, string MimeType, CancellationToken CancellationToken)
        {
            Interlocked.Increment(ref Submissions);
            return Task.FromResult("synthetic-job");
        }
        public Task<TranscriptionResponse> GetResultAsync(Guid JobId, string ProviderJobId, CancellationToken CancellationToken) =>
            Task.FromResult(new TranscriptionResponse(JobId, Complete ? TranscriptionStatus.Completed : TranscriptionStatus.Pending,
                MultipleSpeakers ? [new(0, 0, 100, "Synthetic first speaker", 0.9), new(1, 101, 200, "Synthetic second speaker", 0.9)] : [new(0, 0, 100, "Synthetic transcript", 0.9)]));
    }
}

