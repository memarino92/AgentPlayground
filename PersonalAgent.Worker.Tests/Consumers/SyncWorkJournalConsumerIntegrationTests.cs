using FluentAssertions;
using Npgsql;
using PersonalAgent.Worker.Consumers;
using Testcontainers.PostgreSql;
using Xunit;

namespace PersonalAgent.Worker.Tests.Consumers;

public sealed class SyncWorkJournalConsumerIntegrationTests : IClassFixture<WorkerPostgresVectorFixture>
{
    private readonly WorkerPostgresVectorFixture _fixture;

    public SyncWorkJournalConsumerIntegrationTests(WorkerPostgresVectorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task CreateVectorDataSource_AllowsWritingPgvectorParameter()
    {
        await _fixture.ResetAsync();

        await using var dataSource = SyncWorkJournalConsumer.CreateVectorDataSource(_fixture.ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();

        var sql = """
            INSERT INTO work_journal_entries (id, entry_date, file_name, content, embedding)
            VALUES (@id, @entry_date, @file_name, @content, @embedding);
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("entry_date", new DateTime(2026, 4, 3));
        cmd.Parameters.AddWithValue("file_name", "2026_04.md");
        cmd.Parameters.AddWithValue("content", "## 04/03\n- Reviewed PRs.");
        cmd.Parameters.Add(new NpgsqlParameter("embedding", new Pgvector.Vector(new float[] { 0.1f, 0.2f, 0.3f })) { DataTypeName = "vector" });

        var written = await cmd.ExecuteNonQueryAsync();
        written.Should().Be(1);
    }
}

public sealed class WorkerPostgresVectorFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:0.8.2-pg18-trixie")
        .WithDatabase("agentplayground_worker_tests")
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
}
