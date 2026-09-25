using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PersonalAgent.Contracts.Automations;
using PersonalAgent.Integrations;
using Testcontainers.PostgreSql;
using Xunit;

namespace PersonalAgent.AutomationRunner.Tests;

public sealed class RailwayProgramTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer Database = new PostgreSqlBuilder("postgres:18").Build();
    private AutomationRuntimeStore Runtime = null!;
    private AutomationSandboxStore Leases = null!;
    public async Task InitializeAsync()
    {
        await Database.StartAsync();
        var Storage = new IntegrationDatabase(Database.GetConnectionString(), Convert.ToBase64String(new byte[32]));
        Runtime = new(Storage); Leases = new(Storage, Runtime);
        await Runtime.InitializeAsync(default);
        await Runtime.SaveAsync(new(0, new() { Enabled = true, EnvironmentId = Guid.NewGuid().ToString(), Checkpoint = "base-v1",
            ImageId = "sha256:" + new string('a', 64), GatewayUrl = "https://api.example.test" }, "replace", "controller-secret"), default);
    }
    public Task DisposeAsync() => Database.DisposeAsync().AsTask();

    [Fact]
    public async Task DurableIdentityPrecedesExecution_AndReplayUsesStoredResult()
    {
        var Message = new ExecuteAutomationProgram(Guid.NewGuid(), 0, "Console.Write(1);", "", DateTimeOffset.UtcNow.AddMinutes(3));
        var Client = new FakeClient(async Request =>
        {
            if (Request.GetProperty("operation").GetString() == "execute")
            {
                var Capability = Request.GetProperty("gatewayToken").GetString()!;
                var Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Capability)));
                (await Leases.AuthorizeAsync(Message.RunId, 0, Hash, default)).Should().BeTrue();
            }
        });
        var Executor = new RailwayProgramExecutor(Runtime, Leases, Client, NullLogger<RailwayProgramExecutor>.Instance);
        var Result = await Executor.ExecuteAsync(Message, default);
        Result.Output.Should().Be("1"); Result.Evidence.SandboxId.Should().Be("test-sandbox");
        (await Executor.ExecuteAsync(Message, default)).Should().Be(Result);
        Client.Operations.Should().Equal("create", "execute", "destroy");
        (await Leases.CleanupAsync(default)).Should().BeEmpty();
    }

    [Fact]
    public async Task InfrastructureFailureStillDestroys_AndCleanupFailureStaysDurable()
    {
        var Client = new FakeClient(_ => Task.CompletedTask) { FailExecution = true, FailDestroy = true };
        var Executor = new RailwayProgramExecutor(Runtime, Leases, Client, NullLogger<RailwayProgramExecutor>.Instance);
        var Message = new ExecuteAutomationProgram(Guid.NewGuid(), 0, "source", "", DateTimeOffset.UtcNow.AddMinutes(3));
        var Result = await Executor.ExecuteAsync(Message, default);
        Result.Evidence.Status.Should().Be("InfrastructureFailed");
        var Cleanup = (await Leases.CleanupAsync(default)).Should().ContainSingle().Subject;
        Cleanup.SandboxId.Should().Be("test-sandbox"); Cleanup.Credential.Should().Be("controller-secret");
        (await Executor.ExecuteAsync(Message, default)).Should().Be(Result);
        Client.Operations.Count(O => O == "execute").Should().Be(1);
    }

    [Fact]
    public async Task ExpiredLeaseRejectsGatewayAndIsFoundAfterRestart()
    {
        var Id = Guid.NewGuid();
        await Leases.ClaimAsync(Id, 0, await Runtime.ReadAsync(default), "hash", default);
        await Leases.AttachAsync(Id, 0, "orphan-id", default);
        await using var Connection = new NpgsqlConnection(Database.GetConnectionString()); await Connection.OpenAsync();
        await using var Command = new NpgsqlCommand("UPDATE app.automation_sandboxes SET expires_at = now() - interval '1 second' WHERE run_id = @run", Connection);
        Command.Parameters.AddWithValue("run", Id); await Command.ExecuteNonQueryAsync();
        var Restarted = new AutomationSandboxStore(new(Database.GetConnectionString(), Convert.ToBase64String(new byte[32])), Runtime);
        (await Restarted.AuthorizeAsync(Id, 0, "hash", default)).Should().BeFalse();
        (await Restarted.CleanupAsync(default)).Should().ContainSingle().Which.SandboxId.Should().Be("orphan-id");
    }

    private sealed class FakeClient(Func<JsonElement, Task> Observe) : IRailwaySandboxClient
    {
        public List<string> Operations { get; } = [];
        public bool FailExecution, FailDestroy;
        public async Task<JsonElement> CallAsync(object Request, CancellationToken Token)
        {
            var Json = JsonSerializer.SerializeToElement(Request);
            Guid.TryParse(Json.GetProperty("environmentId").GetString(), out _).Should().BeTrue();
            var Operation = Json.GetProperty("operation").GetString()!; Operations.Add(Operation);
            await Observe(Json);
            if (Operation == "execute" && FailExecution || Operation == "destroy" && FailDestroy) throw new InvalidOperationException("Private provider error that must not escape");
            return Operation switch
            {
                "create" => JsonSerializer.SerializeToElement(new { id = "test-sandbox" }),
                "execute" => JsonSerializer.SerializeToElement(new { stdout = "1", stderr = "", status = "Completed", exitCode = 0 }),
                _ => JsonSerializer.SerializeToElement(new { destroyed = true })
            };
        }
    }
}
