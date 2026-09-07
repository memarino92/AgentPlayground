using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using PersonalAgent.Models;
using PersonalAgent.Services;
using Xunit;

namespace PersonalAgent.Tests.Services;

public class ToolAccessServiceTests
{
    [Fact]
    public async Task IsAllowedAsync_UsesRestrictedCoachDefaults()
    {
        var service = new ToolAccessService(new TestStore(), new AgentToolRegistry(new TestTavilyProvider()));

        var checkinsAllowed = await service.IsAllowedAsync(AgentRoles.Coach, AgentToolKeys.SearchCoachCheckins);
        var journalAllowed = await service.IsAllowedAsync(AgentRoles.Coach, AgentToolKeys.SearchWorkJournal);

        checkinsAllowed.Should().BeTrue();
        journalAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task IsAllowedAsync_UsesPersistedOverride()
    {
        var store = new TestStore
        {
            Permissions = new Dictionary<string, bool> { [AgentToolKeys.SearchCoachCheckins] = false }
        };
        var service = new ToolAccessService(store, new AgentToolRegistry(new TestTavilyProvider()));

        var allowed = await service.IsAllowedAsync(AgentRoles.Coach, AgentToolKeys.SearchCoachCheckins);

        allowed.Should().BeFalse();
    }

    [Fact]
    public async Task IsAllowedAsync_DeniesNewMcpToolByDefault()
    {
        var service = new ToolAccessService(new TestStore(), new AgentToolRegistry(new TestTavilyProvider([new StubFunction("search")])));

        var allowed = await service.IsAllowedAsync(AgentRoles.Owner, AgentToolKeys.Tavily("search"));

        allowed.Should().BeFalse();
    }

    private sealed class TestStore : IToolAccessStore
    {
        public IReadOnlyDictionary<string, bool> Permissions { get; init; } = new Dictionary<string, bool>();

        public Task<IReadOnlyDictionary<string, bool>> GetRolePermissionsAsync(string role, CancellationToken cancellationToken = default) =>
            Task.FromResult(Permissions);

        public Task SavePermissionsAsync(IReadOnlyList<ToolRolePermission> permissions, string updatedBy, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class TestTavilyProvider(IReadOnlyList<AIFunction>? tools = null) : ITavilyMcpToolProvider
    {
        public bool IsAvailable => true;
        public string Status => "Available";
        public IReadOnlyList<AIFunction> GetTools() => tools ?? [];
    }

    private sealed class StubFunction(string name) : AIFunction
    {
        public override string Name => name;
        public override JsonElement JsonSchema => JsonDocument.Parse("{} ").RootElement;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            ValueTask.FromResult<object?>(null);
    }
}
