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
    [InlineData(null)]
    [InlineData("practice-a.m4a")]
    public async Task FreshChats_AfterServiceRecreation_RetrieveExistingUntaggedTranscript(string? FileName)
    {
        var State = await SeedAsync(SemanticMatch: true);
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
                                ["profileId"] = "athlete-b"
                            })])));
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
