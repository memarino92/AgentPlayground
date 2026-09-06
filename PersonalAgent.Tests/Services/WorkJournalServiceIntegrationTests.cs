using AgentPlayground.Contracts.Messaging;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using PersonalAgent.Services;
using Testcontainers.PostgreSql;
using Xunit;

namespace PersonalAgent.Tests.Services;

public sealed class WorkJournalServiceIntegrationTests : IClassFixture<PostgresVectorFixture>
{
    private readonly PostgresVectorFixture _fixture;

    public WorkJournalServiceIntegrationTests(PostgresVectorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SearchWorkJournalAsync_ReturnsMatchingEntries_FromPgVectorQuery()
    {
        await _fixture.ResetAsync();
        await _fixture.SeedEntryAsync(new DateTime(2026, 4, 3), "## 04/03\n- Did code reviews and triage.", "[0.1,0.2,0.3]");

        var options = Options.Create(new SqlTransportOptions { ConnectionString = _fixture.ConnectionString });
        var bus = Mock.Of<IBus>();
        var embeddingService = new StubEmbeddingService([0.1f, 0.2f, 0.3f]);
        var service = new WorkJournalService(bus, options, embeddingService, NullLogger<WorkJournalService>.Instance);

        var result = await service.SearchWorkJournalAsync("code reviews");

        result.Should().Contain("Found the following journal entries:");
        result.Should().Contain("2026-04-03");
        result.Should().Contain("Did code reviews and triage");
        result.Should().NotContain("An error occurred while searching the work journal");
    }

    private sealed class StubEmbeddingService(float[] embedding) : IAgentEmbeddingService
    {
        public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string content, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReadOnlyMemory<float>>(embedding);

        public Task<List<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IReadOnlyList<string> contents, CancellationToken cancellationToken = default) =>
            Task.FromResult(contents.Select(_ => new ReadOnlyMemory<float>(embedding)).ToList());
    }
}

public sealed class PostgresVectorFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:0.8.2-pg18-trixie")
        .WithDatabase("agentplayground_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        var sql = """
            CREATE EXTENSION IF NOT EXISTS vector;
            DROP TABLE IF EXISTS work_journal_entries;
            CREATE TABLE work_journal_entries (
                id uuid PRIMARY KEY,
                entry_date date NOT NULL,
                file_name text NOT NULL,
                content text NOT NULL,
                embedding vector(3)
            );
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SeedEntryAsync(DateTime entryDate, string content, string embeddingLiteral)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        var sql = $"""
            INSERT INTO work_journal_entries (id, entry_date, file_name, content, embedding)
            VALUES (@id, @entryDate, @fileName, @content, '{embeddingLiteral}'::vector);
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("entryDate", entryDate.Date);
        cmd.Parameters.AddWithValue("fileName", "2026_04.md");
        cmd.Parameters.AddWithValue("content", content);
        await cmd.ExecuteNonQueryAsync();
    }
}
