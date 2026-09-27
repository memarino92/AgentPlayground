using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using PersonalAgent.Api.Configuration;
using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;
using PersonalAgent.Contracts.Messaging.Commands;
using Xunit;

namespace PersonalAgent.Api.Tests.Services;

public sealed class CoachCallExecutiveSummaryTests(PostgresVectorFixture Database) : IClassFixture<PostgresVectorFixture>
{
    [Fact]
    public async Task HistoricalCompletedCall_GetsRoleGroupedExecutiveSummary_OnlyOnce()
    {
        var Memory = new AgentMemoryOptions { ConnectionString = Database.ConnectionString,
            Schema = "summary_" + Guid.NewGuid().ToString("N"), VectorDimensions = 3 };
        await new AgentMemorySchemaInitializer(Options.Create(Memory), NullLogger<AgentMemorySchemaInitializer>.Instance).StartAsync(default);
        var Id = Guid.NewGuid();
        await using (var Connection = new NpgsqlConnection(Database.ConnectionString))
        {
            await Connection.OpenAsync();
            await using var WorkflowTable = new NpgsqlCommand("CREATE SCHEMA IF NOT EXISTS automation; CREATE TABLE IF NOT EXISTS automation.\"CoachCallWorkflows\" (\"CorrelationId\" uuid PRIMARY KEY, \"CurrentState\" text NOT NULL)", Connection);
            await WorkflowTable.ExecuteNonQueryAsync();
            await using var Seed = new NpgsqlCommand($"""
                INSERT INTO {Memory.Schema}.coach_call_uploads
                    (upload_id, profile_id, session_id, correlation_id, original_file_name, mime_type, size_bytes, file_hash, status, created_at, updated_at)
                VALUES (@id, 'athlete', @id, @id, 'synthetic.wav', 'audio/wav', 3, 'synthetic', 'Completed', now(), now());
                INSERT INTO {Memory.Schema}.coach_call_sessions
                    (session_id, upload_id, profile_id, transcript_text, summary_markdown, created_at, updated_at)
                VALUES (@id, @id, 'athlete', 'Old transcript', 'Old chunk summary', now(), now());
                INSERT INTO {Memory.Schema}.coach_call_utterances
                    (session_id, speaker_label, speaker_role, start_ms, end_ms, confidence, content)
                VALUES (@id, 0, 'athlete', 0, 1000, 1, 'My knee felt sore after squats.'),
                       (@id, 1, 'coach', 1001, 2000, 1, 'Use a slower descent next week.');
                """, Connection);
            Seed.Parameters.AddWithValue("id", Id);
            await Seed.ExecuteNonQueryAsync();
        }
        var Endpoint = new Mock<ISendEndpoint>();
        var Sender = new Mock<IBus>();
        Sender.Setup(S => S.GetSendEndpoint(It.IsAny<Uri>())).ReturnsAsync(Endpoint.Object);
        await new CoachCallSummaryBackfill(Options.Create(Memory), Sender.Object,
            NullLogger<CoachCallSummaryBackfill>.Instance).SweepAsync(default);
        Endpoint.Verify(E => E.Send(It.Is<GenerateCoachCallSummary>(C => C.UploadId == Id && C.ProfileId == "athlete"),
            It.IsAny<CancellationToken>()), Times.Once);
        var Catalog = new Mock<IChatModelCatalog>();
        Catalog.Setup(C => C.GetDefaultModelAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AvailableChatModel("test-model", "Test", true));
        var Chat = new Mock<IChatClient>();
        Chat.Setup(C => C.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"athleteCheckIn":["Knee felt sore after squats."],"coachFeedbackOnLifts":[],"cuesForNextWeek":["Use a slower squat descent."]}""")));
        var Factory = new Mock<IAgentChatClientFactory>();
        Factory.Setup(F => F.Create("test-model")).Returns(Chat.Object);
        var Service = new CoachCallExecutiveSummaryService(Options.Create(Memory), Factory.Object, Catalog.Object,
            NullLogger<CoachCallExecutiveSummaryService>.Instance);
        var Request = new GenerateCoachCallSummary(Id, Id, "athlete");

        (await Service.GenerateAsync(Request with { ProfileId = "other" }, default)).Should().BeFalse();
        (await Service.GenerateAsync(Request, default)).Should().BeTrue();
        (await Service.GenerateAsync(Request, default)).Should().BeTrue();

        Chat.Verify(C => C.GetResponseAsync(It.Is<IEnumerable<ChatMessage>>(Messages =>
            Messages.Any(M => M.Text.Contains("[athlete] My knee felt sore")) &&
            Messages.Any(M => M.Text.Contains("[coach] Use a slower descent"))),
            It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        await using var VerifyConnection = new NpgsqlConnection(Database.ConnectionString);
        await VerifyConnection.OpenAsync();
        await using var Verify = new NpgsqlCommand($"SELECT summary_markdown, summary_json->>'executiveSummaryVersion' FROM {Memory.Schema}.coach_call_sessions WHERE session_id = @id", VerifyConnection);
        Verify.Parameters.AddWithValue("id", Id);
        await using (var Reader = await Verify.ExecuteReaderAsync())
        {
            (await Reader.ReadAsync()).Should().BeTrue();
            Reader.GetString(0).Should().Contain("Athlete check-in: how the week went").And.Contain("Cues for next week");
            Reader.GetString(0).Should().NotContain("Old chunk summary");
            Reader.GetString(1).Should().Be("1");
        }
        var Checkins = new CoachCheckinService(Options.Create(new SqlTransportOptions { ConnectionString = Database.ConnectionString }),
            Options.Create(Memory), Options.Create(new CoachCheckinOptions()), Mock.Of<IAgentEmbeddingService>(),
            NullLogger<CoachCheckinService>.Instance);
        (await Checkins.GetRecentUploadsAsync("athlete")).Single().ExecutiveSummary.Should().Contain("Use a slower squat descent.");
    }
}
