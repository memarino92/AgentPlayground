using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using PersonalAgent.Api.Configuration;
using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;
using PersonalAgent.Contracts.Messaging;
using Xunit;

namespace PersonalAgent.Api.Tests.Services;

public sealed class CoachingUploadDatabaseTests(PostgresVectorFixture Database) : IClassFixture<PostgresVectorFixture>
{
    [Fact]
    public async Task ConcurrentOwnedIngestion_QueuesOnce_AndForeignUploadReferenceIsNotVisible()
    {
        var (Actions, Memory) = await CreateAsync();
        var Owner = new AgentAccessContext("owner", "Owner", "owner");
        var Results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Actions.UploadAsync(Owner, "synthetic.m4a", "audio/mp4", [1, 2, 3], default)));
        Results.Select(R => R.UploadId).Distinct().Should().ContainSingle();
        Results.Count(R => R.IsDuplicate).Should().Be(1);
        var Status = await Actions.StatusAsync(Owner, Results[0].UploadId, default);
        Status!.Status.Should().Be(CoachCallUploadStatus.Uploaded);
        (await Actions.StatusAsync(new("other", "Owner", "other"), Results[0].UploadId, default)).Should().BeNull();
        await using var Connection = new NpgsqlConnection(Database.ConnectionString);
        await Connection.OpenAsync();
        await using var Command = new NpgsqlCommand($"SELECT count(*) FROM {CoachCallOutbox.Table(Memory.Schema)} WHERE message_type = 'StartCoachCallWorkflow'", Connection);
        ((long)(await Command.ExecuteScalarAsync())!).Should().Be(1);
    }

    [Fact]
    public async Task QueuePersistenceFailure_RollsBackUploadAndSession()
    {
        var (Actions, Memory) = await CreateAsync();
        await using var Connection = new NpgsqlConnection(Database.ConnectionString);
        await Connection.OpenAsync();
        await using var Command = new NpgsqlCommand($"DROP TABLE {CoachCallOutbox.Table(Memory.Schema)}", Connection);
        await Command.ExecuteNonQueryAsync();
        await Actions.Invoking(A => A.UploadAsync(new("owner", "Owner", "owner"), "synthetic.m4a", "audio/mp4", [4, 5, 6], default)).Should().ThrowAsync<PostgresException>();
        Command.CommandText = $"SELECT (SELECT count(*) FROM {Memory.Schema}.coach_call_uploads) + (SELECT count(*) FROM {Memory.Schema}.coach_call_sessions)";
        ((long)(await Command.ExecuteScalarAsync())!).Should().Be(0);
    }

    private async Task<(CoachUploadActions Actions, AgentMemoryOptions Memory)> CreateAsync()
    {
        var Memory = new AgentMemoryOptions { ConnectionString = Database.ConnectionString, Schema = "chat_upload_" + Guid.NewGuid().ToString("N"), VectorDimensions = 3 };
        await new AgentMemorySchemaInitializer(Options.Create(Memory), NullLogger<AgentMemorySchemaInitializer>.Instance).StartAsync(default);
        var Settings = Options.Create(new CoachCheckinOptions());
        var Store = new CoachCheckinService(Options.Create(new SqlTransportOptions { ConnectionString = Database.ConnectionString }),
            Options.Create(Memory), Settings, Mock.Of<IAgentEmbeddingService>(), NullLogger<CoachCheckinService>.Instance);
        return (new(Store, Settings), Memory);
    }
}
