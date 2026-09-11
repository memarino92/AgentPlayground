using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AgentPlayground.Contracts.Messaging;
using MassTransit;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using PersonalAgent.Configuration;
using PersonalAgent.Endpoints;
using PersonalAgent.Models;
using PersonalAgent.Security;
using PersonalAgent.Services;
using PersonalAgent.Tests.Services;
using Xunit;

namespace PersonalAgent.Tests.Endpoints;

public sealed class CoachEvidenceEndpointsTests(PostgresVectorFixture Database) : IClassFixture<PostgresVectorFixture>
{
    [Fact]
    public async Task RangePlayback_DeletionAndReplacementHost_PreserveReadableEvidence()
    {
        var State = await SeedAsync();
        await using (var App = await CreateAsync(State.Options))
        {
            using var Client = ClientFor(App, "owner", "Owner");
            using var Range = new HttpRequestMessage(HttpMethod.Get, $"/api/coach-checkins/{State.Id}/audio?profileId=owner");
            Range.Headers.Range = new(1, 2);
            using var Response = await Client.SendAsync(Range);
            Response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
            (await Response.Content.ReadAsByteArrayAsync()).Should().Equal(2, 3);
            Response.Content.Headers.ContentRange!.ToString().Should().Be("bytes 1-2/4");
            Response.Headers.CacheControl!.NoStore.Should().BeTrue();
            using var Invalid = new HttpRequestMessage(HttpMethod.Get, Range.RequestUri);
            Invalid.Headers.Range = new(9, 12);
            (await Client.SendAsync(Invalid)).StatusCode.Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);
            (await Client.DeleteAsync(Range.RequestUri)).StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await Client.DeleteAsync(Range.RequestUri)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
        await using var Restarted = await CreateAsync(State.Options);
        using var RestartedClient = ClientFor(Restarted, "owner", "Owner");
        (await RestartedClient.GetAsync($"/api/coach-checkins/{State.Id}/audio?profileId=owner")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var Evidence = await RestartedClient.GetFromJsonAsync<CoachEvidenceResponse>($"/api/coach-checkins/{State.Id}/evidence?profileId=owner");
        Evidence!.AudioAvailable.Should().BeFalse();
        Evidence.Transcript.TranscriptText.Should().Be("Synthetic evidence");
        Evidence.Transcript.SessionId.Should().Be(State.Id);
    }

    [Theory]
    [InlineData("other", "Owner", "owner", false, 403)]
    [InlineData("other", "Owner", "other", false, 404)]
    [InlineData("coach", "Coach", "owner", true, 200)]
    [InlineData("coach", "Coach", "owner", false, 403)]
    public async Task ReadsAndRanges_RequireCurrentSubjectAccess(string Actor, string Role, string Profile, bool Assigned, int Expected)
    {
        var State = await SeedAsync();
        var Assignments = new Mock<ICoachAssignmentStore>();
        Assignments.Setup(Store => Store.GetAssignedProfilesAsync("coach", "coach@example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Assigned ? new[] { "owner" } : Array.Empty<string>());
        await using var App = await CreateAsync(State.Options, Assignments.Object);
        using var Client = ClientFor(App, Actor, Role);
        foreach (var Suffix in new[] { "evidence", "audio" })
            ((int)(await Client.GetAsync($"/api/coach-checkins/{State.Id}/{Suffix}?profileId={Profile}")).StatusCode).Should().Be(Expected);
        using var Range = new HttpRequestMessage(HttpMethod.Get, $"/api/coach-checkins/{State.Id}/audio?profileId={Profile}");
        Range.Headers.Range = new(0, 1);
        ((int)(await Client.SendAsync(Range)).StatusCode).Should().Be(Expected == 200 ? 206 : Expected);
        if (Role != "Coach") return;
        (await Client.DeleteAsync(Range.RequestUri)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Assignments.Setup(Store => Store.GetAssignedProfilesAsync("coach", "coach@example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        (await Client.GetAsync(Range.RequestUri)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("Uploaded")]
    [InlineData("Transcribing")]
    [InlineData("Processing")]
    [InlineData("AwaitingSpeakerOverride")]
    public async Task PendingDeletion_IsRejectedWithoutRemovingAudio(string Status)
    {
        var State = await SeedAsync(Status);
        await using var App = await CreateAsync(State.Options);
        using var Client = ClientFor(App, "owner", "Owner");
        var Uri = $"/api/coach-checkins/{State.Id}/audio?profileId=owner";
        (await Client.DeleteAsync(Uri)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Client.GetByteArrayAsync(Uri)).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task MissingOrForgedSignature_CannotReadOrDelete()
    {
        var State = await SeedAsync();
        await using var App = await CreateAsync(State.Options);
        using var Client = App.GetTestClient();
        Client.DefaultRequestHeaders.Add("X-Internal-Api-Key", "internal");
        foreach (var Method in new[] { HttpMethod.Get, HttpMethod.Delete })
        {
            using var Request = new HttpRequestMessage(Method, $"/api/coach-checkins/{State.Id}/audio?profileId=owner");
            (await Client.SendAsync(Request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        using var Forged = ClientFor(App, "owner", "Owner");
        Forged.DefaultRequestHeaders.Remove("X-Agent-Signature");
        Forged.DefaultRequestHeaders.Add("X-Agent-Signature", "invalid");
        (await Forged.GetAsync($"/api/coach-checkins/{State.Id}/evidence?profileId=owner")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<(AgentMemoryOptions Options, Guid Id)> SeedAsync(string Status = "Completed")
    {
        var Memory = new AgentMemoryOptions { ConnectionString = Database.ConnectionString, Schema = "evidence_" + Guid.NewGuid().ToString("N"), VectorDimensions = 3 };
        await new AgentMemorySchemaInitializer(Options.Create(Memory), NullLogger<AgentMemorySchemaInitializer>.Instance).StartAsync(default);
        var Id = Guid.NewGuid();
        await using var Connection = new NpgsqlConnection(Database.ConnectionString);
        await Connection.OpenAsync();
        await using var Command = new NpgsqlCommand($"""
            INSERT INTO {Memory.Schema}.coach_call_uploads
                (upload_id, profile_id, session_id, correlation_id, original_file_name, mime_type, size_bytes, file_hash, audio_bytes, status, created_at, updated_at)
            VALUES (@id, 'owner', @id, @id, 'synthetic.wav', 'audio/wav', 4, 'synthetic', decode('01020304','hex'), @status, now(), now());
            INSERT INTO {Memory.Schema}.coach_call_sessions (session_id, upload_id, profile_id, transcript_text, created_at, updated_at)
            VALUES (@id, @id, 'owner', 'Synthetic evidence', now(), now());
            """, Connection);
        Command.Parameters.AddWithValue("id", Id);
        Command.Parameters.AddWithValue("status", Status);
        await Command.ExecuteNonQueryAsync();
        return (Memory, Id);
    }

    private static async Task<WebApplication> CreateAsync(AgentMemoryOptions Memory, ICoachAssignmentStore? Assignments = null)
    {
        var Builder = WebApplication.CreateBuilder();
        Builder.WebHost.UseTestServer();
        Builder.Services.AddSingleton(Options.Create(Memory));
        Builder.Services.AddSingleton(Options.Create(new SqlTransportOptions { ConnectionString = Memory.ConnectionString }));
        Builder.Services.AddSingleton(Options.Create(new CoachCheckinOptions()));
        Builder.Services.AddSingleton(Mock.Of<IBus>());
        Builder.Services.AddSingleton(Mock.Of<IAgentEmbeddingService>());
        Builder.Services.AddSingleton(Assignments ?? Mock.Of<ICoachAssignmentStore>());
        Builder.Services.AddSingleton<CoachCheckinService>();
        Builder.Services.AddSingleton<ICoachEvidenceService, CoachEvidenceService>();
        var App = Builder.Build();
        App.MapGroup("/api").AddEndpointFilter(new InternalApiKeyFilter(Options.Create(new ApiKeyOptions { InternalApiKey = "internal" })))
            .MapCoachEvidence(Options.Create(new SecurityOptions { ActorSigningKey = "signing-key" }));
        await App.StartAsync();
        return App;
    }

    private static HttpClient ClientFor(WebApplication App, string Actor, string Role)
    {
        var Client = App.GetTestClient();
        var Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var Email = "coach@example.test";
        var Signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("signing-key"), Encoding.UTF8.GetBytes($"{Actor}\n{Role}\n{Email}\n{Timestamp}")));
        Client.DefaultRequestHeaders.Add("X-Internal-Api-Key", "internal");
        Client.DefaultRequestHeaders.Add("X-Agent-Actor", Actor);
        Client.DefaultRequestHeaders.Add("X-Agent-Role", Role);
        Client.DefaultRequestHeaders.Add("X-Agent-Email", Email);
        Client.DefaultRequestHeaders.Add("X-Agent-Timestamp", Timestamp);
        Client.DefaultRequestHeaders.Add("X-Agent-Signature", Signature);
        return Client;
    }
}
