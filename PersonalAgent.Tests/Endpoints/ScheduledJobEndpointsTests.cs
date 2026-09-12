using System.Net;
using System.Net.Http.Json;
using AgentPlayground.Contracts.Configuration;
using AgentPlayground.Integrations;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
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

public class ScheduledJobEndpointsTests(PostgresVectorFixture Database) : IClassFixture<PostgresVectorFixture>
{
    [Theory]
    [InlineData("AgentTask")]
    [InlineData("Notification")]
    public async Task ListDetailAndCancelEnforceCurrentSubjectAndActor(string JobType)
    {
        var (Store, _) = await new ScheduledJobTests(Database).SetupAsync();
        var Own = ScheduledJobTests.Job() with { JobType = JobType, Notification = JobType == "Notification" ? new("Title", "Body", null) : null };
        var Coach = Own with { TaskId = Guid.NewGuid(), ActorId = "coach", ActorEmail = "coach@example.test", SourceSessionId = Guid.NewGuid().ToString() };
        await Store.CreateAsync(Own, default);
        await Store.CreateAsync(Coach, default);
        var Policy = new Mock<IScheduledActorPolicy>();
        Policy.Setup(P => P.ResolveRoleAsync("owner", null, It.IsAny<CancellationToken>())).ReturnsAsync("Owner");
        Policy.Setup(P => P.ResolveRoleAsync("other", null, It.IsAny<CancellationToken>())).ReturnsAsync("Owner");
        Policy.Setup(P => P.ResolveRoleAsync("coach", "coach@example.test", It.IsAny<CancellationToken>())).ReturnsAsync("Coach");
        var Assignments = new Mock<ICoachAssignmentStore>();
        Assignments.Setup(A => A.GetAssignedProfilesAsync("coach", "coach@example.test", It.IsAny<CancellationToken>())).ReturnsAsync(["owner"]);
        var Builder = WebApplication.CreateBuilder();
        Builder.WebHost.UseTestServer();
        Builder.Services.AddSingleton(Store);
        Builder.Services.AddSingleton(Policy.Object);
        Builder.Services.AddSingleton(Assignments.Object);
        Builder.Services.AddSingleton(Mock.Of<IToolAccessStore>());
        Builder.Services.AddSingleton(Mock.Of<IAgentToolRegistry>());
        Builder.Services.AddSingleton<ToolAccessService>();
        Builder.Services.AddSingleton<ScheduledJobAuthorization>();
        Builder.Services.AddSingleton(Mock.Of<IScheduledJobRunner>());
        Builder.Services.AddSingleton<ScheduledJobExecutionService>();
        await using var App = Builder.Build();
        // Inject a verified actor after a valid signature filter, as production does.
        var Security = Options.Create(new SecurityOptions { ActorSigningKey = "synthetic-key" });
        App.MapGroup("/api").MapScheduledJobs(Security);
        await App.StartAsync();
        using var Client = App.GetTestClient();
        Sign(Client, "other", "Owner");
        (await Client.GetAsync("/api/jobs/?profileId=owner")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Client.GetAsync($"/api/jobs/{Own.TaskId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Client.PostAsync($"/api/jobs/{Own.TaskId}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        Sign(Client, "coach", "Coach", "coach@example.test");
        (await Client.GetFromJsonAsync<List<ScheduledJob>>("/api/jobs/?profileId=owner"))!.Should().ContainSingle().Which.TaskId.Should().Be(Coach.TaskId);
        (await Client.GetAsync($"/api/jobs/{Own.TaskId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        Assignments.Setup(A => A.GetAssignedProfilesAsync("coach", "coach@example.test", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        (await Client.GetAsync($"/api/jobs/{Coach.TaskId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        Sign(Client, "owner", "Owner");
        (await Client.GetFromJsonAsync<ScheduledJobDetail>($"/api/jobs/{Coach.TaskId}"))!.Job.SourceSessionId.Should().BeNull();
        (await Client.PostAsync($"/api/jobs/{Own.TaskId}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        Client.DefaultRequestHeaders.Remove("X-Agent-Signature");
        (await Client.GetAsync("/api/jobs/?profileId=owner")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DatabasePolicyReadsLiveWebOverridesAndEncryptedValues()
    {
        const string Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
        var Policy = new DatabaseScheduledActorPolicy(new IntegrationDatabase(Database.ConnectionString, Key));
        await using var Connection = new NpgsqlConnection(Database.ConnectionString);
        await Connection.OpenAsync();
        await using var Command = new NpgsqlCommand("""
            CREATE SCHEMA IF NOT EXISTS app;
            CREATE TABLE IF NOT EXISTS app.configuration_settings(scope text, key text, value text, is_secret boolean, is_active boolean);
            DELETE FROM app.configuration_settings;
            INSERT INTO app.configuration_settings VALUES ('Shared', 'Authentication:Schemes:GitHub:AllowedUsers', 'old-owner', false, true);
            INSERT INTO app.configuration_settings VALUES ('Web', 'Authentication:Schemes:GitHub:AllowedUsers', @value, true, true);
            """, Connection);
        Command.Parameters.AddWithValue("value", PostgresConfigurationCrypto.Encrypt("owner", "Web", "Authentication:Schemes:GitHub:AllowedUsers", Key));
        await Command.ExecuteNonQueryAsync();
        (await Policy.ResolveRoleAsync("owner", null, default)).Should().Be("Owner");
        (await Policy.ResolveRoleAsync("old-owner", null, default)).Should().BeNull();
        Command.CommandText = "UPDATE app.configuration_settings SET value = '', is_secret = false WHERE scope = 'Web'";
        await Command.ExecuteNonQueryAsync();
        (await Policy.ResolveRoleAsync("owner", null, default)).Should().BeNull();
    }

    private static void Sign(HttpClient Client, string Actor, string Role, string Email = "")
    {
        Client.DefaultRequestHeaders.Clear();
        var Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var Payload = $"{Actor}\n{Role}\n{Email}\n{Timestamp}";
        Client.DefaultRequestHeaders.Add("X-Agent-Actor", Actor);
        Client.DefaultRequestHeaders.Add("X-Agent-Role", Role);
        Client.DefaultRequestHeaders.Add("X-Agent-Email", Email);
        Client.DefaultRequestHeaders.Add("X-Agent-Timestamp", Timestamp);
        Client.DefaultRequestHeaders.Add("X-Agent-Signature", Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes("synthetic-key"), System.Text.Encoding.UTF8.GetBytes(Payload))));
    }
}
