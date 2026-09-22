using System.Net;
using System.Text;
using System.Text.Json;

using PersonalAgent.Contracts.Messaging.Commands;
using PersonalAgent.Contracts.Messaging.Requests;
using PersonalAgent.Contracts.Messaging.Responses;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using PersonalAgent.Worker.Configuration;
using PersonalAgent.Worker.Consumers;
using Testcontainers.PostgreSql;
using Xunit;

namespace PersonalAgent.Worker.Tests.Consumers;

public sealed class SyncWorkJournalConsumerIntegrationTests : IClassFixture<WorkerPostgresVectorFixture>
{
    private readonly WorkerPostgresVectorFixture _fixture;

    public SyncWorkJournalConsumerIntegrationTests(WorkerPostgresVectorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Consume_UnchangedShaSkipsDownloadAndAllModelRequests()
    {
        await _fixture.ResetAsync();
        using var Sync = new SyncScenario(_fixture.ConnectionString);
        await Sync.RunAsync();
        await Sync.RunAsync();

        Sync.Downloads.Should().Be(1);
        Sync.ParseCalls.Should().Be(1);
        Sync.EmbeddingCalls.Should().Be(1);
        (await ReadScalarAsync("SELECT count(*) FROM work_journal_file_sync")).Should().Be(1L);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consume_ChangedShaProcessesFileAndOnlyEmbedsChangedEntries(bool EntryChanged)
    {
        await _fixture.ResetAsync();
        using var Sync = new SyncScenario(_fixture.ConnectionString);
        await Sync.RunAsync();
        Sync.Content = "Updated source markdown";
        if (EntryChanged) Sync.Entries = [new(new DateTime(2026, 4, 3), "Updated entry")];
        await Sync.RunAsync();
        await Sync.RunAsync();

        Sync.Downloads.Should().Be(2);
        Sync.ParseCalls.Should().Be(2);
        Sync.EmbeddingCalls.Should().Be(EntryChanged ? 2 : 1);
        (await ReadScalarAsync("SELECT blob_sha FROM work_journal_file_sync")).Should().Be(Sync.Sha);
        (await ReadScalarAsync("SELECT content FROM work_journal_entries")).Should().Be(Sync.Entries[0].Content);
    }

    [Fact]
    public async Task Consume_EmptyFileIsCheckpointed()
    {
        await _fixture.ResetAsync();
        using var Sync = new SyncScenario(_fixture.ConnectionString) { Entries = [] };
        await Sync.RunAsync();
        await Sync.RunAsync();
        Sync.Downloads.Should().Be(1);
        Sync.ParseCalls.Should().Be(1);
        Sync.EmbeddingCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("parse")]
    [InlineData("embedding")]
    [InlineData("write")]
    [InlineData("download")]
    public async Task Consume_FailurePreservesCheckpointAndRetries(string Failure)
    {
        await _fixture.ResetAsync();
        using var Sync = new SyncScenario(_fixture.ConnectionString);
        await Sync.RunAsync();
        var PreviousSha = Sync.Sha;
        Sync.Content = "Changed markdown";
        Sync.Entries = [new(new DateTime(2026, 4, 3), "Changed entry"), new(new DateTime(2026, 4, 4), "Second entry")];
        Sync.Failure = Failure;
        await Sync.RunAsync();

        (await ReadScalarAsync("SELECT blob_sha FROM work_journal_file_sync")).Should().Be(PreviousSha);
        (await ReadScalarAsync("SELECT content FROM work_journal_entries")).Should().Be("Original entry");
        (await ReadScalarAsync("SELECT count(*) FROM work_journal_entries")).Should().Be(1L);

        Sync.Failure = null;
        await Sync.RunAsync();
        await Sync.RunAsync();
        Sync.Downloads.Should().Be(3);
        (await ReadScalarAsync("SELECT blob_sha FROM work_journal_file_sync")).Should().Be(Sync.Sha);
        (await ReadScalarAsync("SELECT count(*) FROM work_journal_entries")).Should().Be(2L);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("repo")]
    [InlineData("branch")]
    [InlineData("path")]
    public async Task Consume_CheckpointIsScopedToSource(string ChangedScope)
    {
        await _fixture.ResetAsync();
        using var Sync = new SyncScenario(_fixture.ConnectionString);
        await Sync.RunAsync();
        switch (ChangedScope)
        {
            case "owner": Sync.Options.RepoOwner = "other-owner"; break;
            case "repo": Sync.Options.RepoName = "other-repo"; break;
            case "branch": Sync.Options.Branch = "other-branch"; break;
            case "path": Sync.Path = "other/journal.md"; break;
        }
        await Sync.RunAsync();
        Sync.Downloads.Should().Be(2);
        Sync.ParseCalls.Should().Be(2);
        (await ReadScalarAsync("SELECT count(*) FROM work_journal_file_sync")).Should().Be(2L);
    }

    [Fact]
    public async Task Consume_MissingShaRemainsUncached()
    {
        await _fixture.ResetAsync();
        using var Sync = new SyncScenario(_fixture.ConnectionString) { OmitSha = true };
        await Sync.RunAsync();
        await Sync.RunAsync();
        Sync.ParseCalls.Should().Be(2);
        (await ReadScalarAsync("SELECT count(*) FROM work_journal_file_sync")).Should().Be(0L);
    }

    private async Task<object?> ReadScalarAsync(string Sql)
    {
        await using var Connection = new NpgsqlConnection(_fixture.ConnectionString);
        await Connection.OpenAsync();
        await using var Command = new NpgsqlCommand(Sql, Connection);
        return await Command.ExecuteScalarAsync();
    }

    private sealed class SyncScenario : HttpMessageHandler
    {
        private readonly HttpClient Client;
        private readonly SyncWorkJournalConsumer Consumer;
        public GitHubOptions Options { get; } = new() { RepoOwner = "owner", RepoName = "repo", JournalPath = "journal" };
        public string Path { get; set; } = "journal/journal.md";
        public string Content { get; set; } = "Original markdown";
        public string Sha => SyncWorkJournalConsumer.GetBlobSha(Encoding.UTF8.GetBytes(Content));
        public bool OmitSha { get; set; }
        public string? Failure { get; set; }
        public List<ParsedWorkJournalEntry> Entries { get; set; } = [new(new DateTime(2026, 4, 3), "Original entry")];
        public int Downloads { get; private set; }
        public int ParseCalls { get; private set; }
        public int EmbeddingCalls { get; private set; }

        public SyncScenario(string ConnectionString)
        {
            Client = new HttpClient(this, disposeHandler: false);
            var Factory = new Mock<IHttpClientFactory>();
            Factory.Setup(Value => Value.CreateClient("GitHubWorkJournal")).Returns(Client);
            var Parser = new Mock<IRequestClient<ParseWorkJournalEntriesRequest>>();
            Parser.Setup(Value => Value.GetResponse<ParseWorkJournalEntriesResponse>(It.IsAny<ParseWorkJournalEntriesRequest>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    ParseCalls++;
                    if (Failure == "parse") throw new InvalidOperationException("Synthetic parse failure");
                    var Response = Mock.Of<Response<ParseWorkJournalEntriesResponse>>(Value => Value.Message == new ParseWorkJournalEntriesResponse(Entries));
                    return Task.FromResult(Response);
                });
            var Embedder = new Mock<IRequestClient<GenerateEmbeddingsRequest>>();
            Embedder.Setup(Value => Value.GetResponse<GenerateEmbeddingsResponse>(It.IsAny<GenerateEmbeddingsRequest>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    EmbeddingCalls++;
                    if (Failure == "embedding") throw new InvalidOperationException("Synthetic embedding failure");
                    List<float[]> Vectors = Entries.Select((_, Index) => Failure == "write" && Index == 1 ? new float[] { 1f, 0f } : [1f, 0f, 0f]).ToList();
                    var Response = Mock.Of<Response<GenerateEmbeddingsResponse>>(Value => Value.Message == new GenerateEmbeddingsResponse(Vectors, 3));
                    return Task.FromResult(Response);
                });
            Consumer = new SyncWorkJournalConsumer(NullLogger<SyncWorkJournalConsumer>.Instance, Factory.Object,
                Microsoft.Extensions.Options.Options.Create(Options), Parser.Object, Embedder.Object,
                Microsoft.Extensions.Options.Options.Create(new SqlTransportOptions { ConnectionString = ConnectionString }));
        }

        public Task RunAsync() => Consumer.Consume(Mock.Of<ConsumeContext<SyncWorkJournalCommand>>());

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken CancellationToken)
        {
            if (Request.RequestUri!.Host == "api.github.com")
            {
                var Listing = JsonSerializer.Serialize(new[] { new { type = "file", name = "journal.md", path = Path, sha = OmitSha ? null : Sha, download_url = "https://journal.test/file.md" } });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Listing) });
            }
            Downloads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Failure == "download" ? "Racing edit" : Content) });
        }

        protected override void Dispose(bool Disposing)
        {
            if (Disposing) Client.Dispose();
            base.Dispose(Disposing);
        }
    }

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
            DROP TABLE IF EXISTS work_journal_file_sync;
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
