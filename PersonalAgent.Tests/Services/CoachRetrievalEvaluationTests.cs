using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using PersonalAgent.Configuration;
using PersonalAgent.Models;
using PersonalAgent.Services;
using Xunit;

namespace PersonalAgent.Tests.Services;

// Synthetic retrieval baseline v1. Deliberately adversarial vectors keep lexical recovery measurable.
public sealed class CoachRetrievalEvaluationTests(PostgresVectorFixture Database) : IClassFixture<PostgresVectorFixture>
{
    private const string Cue = "coach: For the yoke carry, inhale during the approach to the pickup. Restart immediately after the turn instead of waiting beside the implement.";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown-exercise")]
    public async Task SemanticRanking_FindsParaphrasedCueWithoutAnyMatchingTag(string? Tag)
    {
        var (Service, Id, _) = await SeedAsync(SemanticMatch: true);
        var Result = await Service.SearchCoachCheckinsAsync("How should I manage my breath and avoid hesitation on loaded carries?", "athlete-a", Tag);
        Result.Should().Contain(Cue).And.NotContain("PRIVATE");
        Result.IndexOf($"/evidence/{Id}", StringComparison.Ordinal).Should().BeLessThan(Result.IndexOf("bench", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("yoke", null)]
    [InlineData(" YOKE ", null)]
    [InlineData("yokes", null)]
    [InlineData("yoke", "practice-a.m4a")]
    [InlineData(null, "PRACTICE-A.M4A")]
    [InlineData("unrecognized exercise", "practice-a.m4a")]
    public async Task YokeCue_RemainsRetrievableWithoutStoredTag(string? Tag, string? FileName)
    {
        var (Service, Id, _) = await SeedAsync();
        var Result = await Service.SearchCoachCheckinsAsync("yoke breathing pickup transition", "athlete-a", Tag, fileName: FileName);
        Result.Should().Contain(Cue);
        Result.Should().Contain($"/evidence/{Id}?profileId=athlete-a&startMs=1000");
        Result.Should().NotContain("PRIVATE");
        if (FileName is not null) Result.Should().NotContain("bench");
    }

    [Theory]
    [InlineData("athlete-a", "missing.m4a")]
    [InlineData("athlete-a", "%")]
    [InlineData("athlete-c", null)]
    public async Task MissingScope_DoesNotClaimAdviceNeverExisted(string Profile, string? FileName)
    {
        var (Service, _, _) = await SeedAsync();
        var Result = await Service.SearchCoachCheckinsAsync("yoke", Profile, "yoke", fileName: FileName);
        Result.Should().Contain("No indexed coach check-in chunks").And.Contain("does not establish");
        Result.Should().NotContain("/evidence/").And.NotContain(Cue).And.NotContain("PRIVATE");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("practice-a.m4a", null)]
    [InlineData(null, "latest")]
    public async Task FreshChats_AfterServiceRecreation_RetrieveExistingUntaggedTranscript(string? FileName, string? Recency)
    {
        var State = Recency == "latest" ? await SeedDatedAsync() : await SeedAsync(SemanticMatch: true);
        // Seed once; neither the worker nor a re-indexing step runs when each application service is recreated.
        for (var Restart = 0; Restart < 2; Restart++)
        {
            var Tavily = new Mock<ITavilyMcpToolProvider>();
            Tavily.Setup(Value => Value.GetTools()).Returns(Array.Empty<AIFunction>());
            var Registry = new AgentToolRegistry(Tavily.Object);
            var Permissions = Registry.GetRegistrations().ToDictionary(Value => Value.Descriptor.Key, Value => Value.Descriptor.Key == AgentToolKeys.SearchCoachCheckins);
            var AccessStore = new Mock<IToolAccessStore>();
            AccessStore.Setup(Value => Value.GetRolePermissionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Permissions);
            var Embeddings = new Mock<IAgentEmbeddingService>();
            Embeddings.Setup(Value => Value.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ReadOnlyMemory<float>([1, 0, 0]));
            var ChatClient = new Mock<IChatClient>();
            var Calls = 0;
            ChatClient.Setup(Value => Value.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
                .Returns((IEnumerable<ChatMessage> Messages, ChatOptions? Options, CancellationToken Token) =>
                {
                    Calls++;
                    if (Calls == 1)
                    {
                        Options!.Tools!.Should().ContainSingle().Which.Name.Should().Be("search_coach_checkins");
                        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                            [new FunctionCallContent("cue-search", "search_coach_checkins", new Dictionary<string, object?>
                            {
                                ["query"] = "How can I breathe better and stop hesitating during loaded carries?",
                                ["exerciseTag"] = null,
                                ["fileName"] = FileName,
                                ["recency"] = Recency,
                                ["profileId"] = "athlete-b"
                            }.Where(Pair => Pair.Value is not null).ToDictionary())])));
                    }
                    var Evidence = Messages.SelectMany(Value => Value.Contents).OfType<FunctionResultContent>().Single().Result!.ToString()!;
                    Evidence.Should().Contain(Cue).And.Contain($"/evidence/{State.Id}?profileId=athlete-a&startMs=1000").And.NotContain("PRIVATE");
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Evidence)));
                });
            var Factory = new Mock<IAgentChatClientFactory>();
            Factory.Setup(Value => Value.Create("test-model")).Returns(ChatClient.Object);
            var Services = new ServiceCollection();
            Services.AddLogging();
            Services.AddSingleton(Options.Create(State.Memory));
            Services.AddSingleton(Options.Create(new ApiKeyOptions()));
            Services.AddSingleton(Options.Create(new CoachCheckinOptions()));
            Services.AddSingleton(Options.Create(new SqlTransportOptions { ConnectionString = Database.ConnectionString }));
            Services.AddSingleton(Mock.Of<IBus>());
            Services.AddSingleton(Embeddings.Object);
            Services.AddSingleton<CoachCheckinService>();
            Services.AddSingleton<IAgentToolRegistry>(Registry);
            Services.AddSingleton(AccessStore.Object);
            Services.AddSingleton<ToolAccessService>();
            Services.AddSingleton<AgentToolBinder>();
            Services.AddSingleton<IAgentSessionStore, PostgresAgentSessionStore>();
            Services.AddSingleton(Mock.Of<IAgentSemanticMemoryStore>());
            Services.AddSingleton<SemanticMemoryService>();
            Services.AddSingleton(Factory.Object);
            var Catalog = new Mock<IChatModelCatalog>();
            Catalog.Setup(Value => Value.FindModelAsync("test-model", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AvailableChatModel("test-model", "Test model", true));
            Services.AddSingleton(Catalog.Object);
            Services.AddSingleton<AgentChatService>();
            await using var Provider = Services.BuildServiceProvider();
            var Chat = Provider.GetRequiredService<AgentChatService>();
            var Session = await Chat.CreateSessionAsync("athlete-a", "test-model");
            var Answer = await Chat.SendMessageAsync(Session.SessionId, "athlete-a", "What did my coach say about my carries?");
            Answer.Should().Contain(Cue);
            Calls.Should().Be(2);
            (await Chat.GetSessionMessagesAsync(Session.SessionId, "athlete-a"))!.Messages.Should().HaveCount(2);
        }
        await using var Connection = new NpgsqlConnection(Database.ConnectionString);
        await Connection.OpenAsync();
        await using var Command = new NpgsqlCommand($"SELECT exercise_tags::text FROM {State.Memory.Schema}.coach_call_chunks WHERE session_id = @id", Connection);
        Command.Parameters.AddWithValue("id", State.Id);
        (await Command.ExecuteScalarAsync()).Should().Be("[]");
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("recent")]
    public async Task RecordingDate_BeatsBatchUploadOrderAndOlderSimilarAdvice(string Mode)
    {
        var State = await SeedDatedAsync();
        var Result = await State.Service.SearchCoachCheckinsAsync("yoke advice", "athlete-a", "yoke", recency: Mode);
        Result.Should().Contain(Cue).And.Contain("2026-08-30 17.12.36.m4a").And.NotContain("PRIVATE");
        if (Mode == "latest") Result.Should().NotContain("OLD cue");
        else Result.IndexOf(Cue, StringComparison.Ordinal).Should().BeLessThan(Result.IndexOf("OLD cue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LatestWithoutChunks_DoesNotFallBackToOlderRecording()
    {
        var State = await SeedDatedAsync();
        await ExecuteAsync($"DELETE FROM {State.Memory.Schema}.coach_call_chunks WHERE session_id = '{State.Id}'");
        var Result = await State.Service.SearchCoachCheckinsAsync("yoke", "athlete-a", "yoke", recency: "latest");
        Result.Should().Contain("No indexed").And.NotContain("OLD cue");
    }

    [Fact]
    public async Task LatestWithoutRelevantAdvice_ReturnsOnlyLatestContext()
    {
        var State = await SeedDatedAsync();
        await ExecuteAsync($"UPDATE {State.Memory.Schema}.coach_call_chunks SET content = 'coach: bench setup' WHERE session_id = '{State.Id}'");
        var Result = await State.Service.SearchCoachCheckinsAsync("yoke", "athlete-a", "yoke", recency: "latest");
        Result.Should().Contain("bench setup").And.NotContain("OLD cue").And.Contain("not guaranteed matches");
    }

    [Fact]
    public async Task UnknownDate_RequiresClarificationButExactFilenameStillWorks()
    {
        var State = await SeedAsync();
        (await State.Service.SearchCoachCheckinsAsync("yoke", "athlete-a", "yoke", recency: "latest"))
            .Should().Contain("Cannot establish").And.NotContain(Cue);
        (await State.Service.SearchCoachCheckinsAsync("yoke", "athlete-a", "yoke", fileName: "practice-a.m4a", recency: "latest"))
            .Should().Contain(Cue).And.Contain("unknown");
    }

    [Fact]
    public async Task HistoricalMode_PreservesSimilarityAndExactOlderScope()
    {
        var State = await SeedDatedAsync();
        var Result = await State.Service.SearchCoachCheckinsAsync("yoke", "athlete-a", "yoke", recency: "relevance");
        Result.Should().Contain("OLD cue").And.NotContain(Cue);
        Result = await State.Service.SearchCoachCheckinsAsync("yoke", "athlete-a", "yoke", fileName: "2026-06-01 10.00.00.m4a", recency: "latest");
        Result.Should().Contain("OLD cue").And.NotContain(Cue);
    }

    [Theory]
    [InlineData("2026-02-30 17.12.36.m4a", false)]
    [InlineData("2026-08-30 25.12.36.m4a", false)]
    [InlineData("2026-08-30 17.12.36.m4a", true)]
    [InlineData("2026-08-30 17.12.36oops.m4a", false)]
    [InlineData("notes.m4a", false)]
    public void RecordingDates_AreValidated(string FileName, bool Valid)
        => CoachRecordingDate.Parse(FileName).HasValue.Should().Be(Valid);

    private async Task<(CoachCheckinService Service, Guid Id, AgentMemoryOptions Memory)> SeedDatedAsync()
    {
        var State = await SeedAsync();
        await ExecuteAsync($"""
            UPDATE {State.Memory.Schema}.coach_call_uploads SET original_file_name = CASE
                WHEN profile_id = 'athlete-b' THEN '2027-01-01 00.00.00.m4a'
                WHEN upload_id = '{State.Id}' THEN '2026-08-30 17.12.36.m4a'
                ELSE '2026-06-01 10.00.00.m4a' END,
                created_at = CASE WHEN upload_id = '{State.Id}' THEN now() - interval '1 day' ELSE now() END;
            UPDATE {State.Memory.Schema}.coach_call_chunks SET content = 'coach: yoke OLD cue', embedding = '[1,0,0]'::vector
                WHERE session_id <> '{State.Id}' AND session_id IN (SELECT session_id FROM {State.Memory.Schema}.coach_call_uploads WHERE profile_id = 'athlete-a');
            UPDATE {State.Memory.Schema}.coach_call_chunks SET embedding = '[1,0.1,0]'::vector WHERE session_id = '{State.Id}';
            """);
        return State;
    }

    [Fact]
    public async Task ExerciseTransition_PreservesSeparateUtteranceCitations()
    {
        var State = await SeedDatedAsync();
        await ExecuteAsync($"""
            INSERT INTO {State.Memory.Schema}.coach_call_utterances (session_id, speaker_label, speaker_role, start_ms, end_ms, confidence, content)
            VALUES ('{State.Id}', 0, 'coach', 0, 500, 1, 'We are reviewing the axle press.'),
                   ('{State.Id}', 0, 'coach', 1000, 9000, 1, 'Aim higher on the dip and drive.'),
                   ('{State.Id}', 0, 'coach', 10000, 30000, 1, 'Now yoke: breathe during the pickup.');
            """);
        var Result = await State.Service.SearchCoachCheckinsAsync("yoke", "athlete-a", "yoke", recency: "latest");
        Result.Should().Contain($"&startMs=1000)").And.Contain($"&startMs=10000)")
            .And.Contain("We are reviewing the axle press").And.Contain("Now yoke").And.Contain("may cross exercise transitions");
    }

    private async Task ExecuteAsync(string Sql)
    {
        await using var Connection = new NpgsqlConnection(Database.ConnectionString);
        await Connection.OpenAsync();
        await using var Command = new NpgsqlCommand(Sql, Connection);
        await Command.ExecuteNonQueryAsync();
    }

    private async Task<(CoachCheckinService Service, Guid Id, AgentMemoryOptions Memory)> SeedAsync(bool SemanticMatch = false)
    {
        var Memory = new AgentMemoryOptions { ConnectionString = Database.ConnectionString, Schema = "retrieval_" + Guid.NewGuid().ToString("N"), VectorDimensions = 3 };
        await new AgentMemorySchemaInitializer(Options.Create(Memory), NullLogger<AgentMemorySchemaInitializer>.Instance).StartAsync(default);
        await using var Connection = new NpgsqlConnection(Database.ConnectionString);
        await Connection.OpenAsync();
        var Target = Guid.NewGuid();
        for (var Index = 0; Index < 8; Index++)
        {
            var Id = Index == 0 ? Target : Guid.NewGuid();
            await using var Command = new NpgsqlCommand($"""
                INSERT INTO {Memory.Schema}.coach_call_uploads
                    (upload_id, profile_id, session_id, correlation_id, original_file_name, mime_type, size_bytes, file_hash, audio_bytes, status, created_at, updated_at)
                VALUES (@id, @profile, @id, @id, @file, 'audio/mp4', 1, @hash, decode('01','hex'), 'Completed', now(), now());
                INSERT INTO {Memory.Schema}.coach_call_sessions (session_id, upload_id, profile_id, transcript_text, created_at, updated_at)
                VALUES (@id, @id, @profile, @content, now(), now());
                INSERT INTO {Memory.Schema}.coach_call_chunks
                    (session_id, chunk_index, start_ms, end_ms, content, speaker_mix, exercise_tags, intent_tags, priority_tags, metadata, embedding, created_at)
                VALUES (@id, 0, 1000, 31000, @content, 'coach_only', '[]', '[]', '[]', jsonb_build_object(), @vector::vector, now());
                """, Connection);
            Command.Parameters.AddWithValue("id", Id);
            Command.Parameters.AddWithValue("profile", Index == 7 ? "athlete-b" : "athlete-a");
            Command.Parameters.AddWithValue("file", Index is 0 or 7 ? "practice-a.m4a" : $"other-{Index}.m4a");
            Command.Parameters.AddWithValue("hash", Id.ToString());
            Command.Parameters.AddWithValue("content", Index == 0 ? Cue : Index == 7 ? "PRIVATE coach: yoke runs cue" : "coach: bench setup cue");
            Command.Parameters.AddWithValue("vector", (Index == 0) == SemanticMatch ? "[1,0,0]" : "[0,1,0]");
            await Command.ExecuteNonQueryAsync();
        }
        var Embeddings = new Mock<IAgentEmbeddingService>();
        Embeddings.Setup(Value => Value.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReadOnlyMemory<float>([1, 0, 0]));
        return (new CoachCheckinService(Mock.Of<IBus>(), Options.Create(new SqlTransportOptions { ConnectionString = Database.ConnectionString }),
            Options.Create(Memory), Options.Create(new CoachCheckinOptions()), Embeddings.Object, NullLogger<CoachCheckinService>.Instance), Target, Memory);
    }
}
