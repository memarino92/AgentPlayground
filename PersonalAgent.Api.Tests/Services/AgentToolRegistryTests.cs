using System.Text.Json;

using PersonalAgent.Contracts.Messaging.Events;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using PersonalAgent.Api.Configuration;
using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;
using Xunit;

namespace PersonalAgent.Api.Tests.Services;

public class AgentToolRegistryTests
{
    private static readonly string[] WebTools = ["tavily_search", "tavily_extract", "tavily_crawl", "tavily_map", "tavily_research"];

    [Fact]
    public async Task Jev_CoversEveryCurrentTool_UsingOnlyAuthorizedApplicationMetadata()
    {
        var Provider = new MutableTavilyProvider { Tools = WebTools.Select(Name => (AIFunction)new StubFunction(Name)).ToArray() };
        var Store = new MutableStore();
        foreach (var Name in WebTools) Store.Permissions[AgentToolKeys.Tavily(Name)] = true;
        using var Services = BuildServices(Provider, Store);
        var Binder = Services.GetRequiredService<AgentToolBinder>();
        var Tools = await Binder.BindAsync(new("owner", AgentRoles.Owner, "subject"));
        Tools.Should().HaveCount(16);
        var DirectMessages = new Dictionary<string, string>
        {
            [AgentToolKeys.PublishMobileNotification] = "notify me: drink water",
            [AgentToolKeys.SyncWorkJournal] = "sync my work journal",
            [AgentToolKeys.SearchWorkJournal] = "search my work journal for database migrations",
            [AgentToolKeys.SearchCoachCheckins] = "search my coaching notes for yoke",
            [AgentToolKeys.ScheduleNotification] = "remind me in 5 minutes to drink water",
            [AgentToolKeys.ScheduleAgentTask] = "schedule a task in 1 hour: check the weather",
            [AgentToolKeys.ListScheduledJobs] = "list my scheduled jobs",
            [AgentToolKeys.GetScheduledJob] = "show scheduled job 11111111-1111-1111-1111-111111111111",
            [AgentToolKeys.UpdateScheduledJob] = "update scheduled job 11111111-1111-1111-1111-111111111111 instruction: check the forecast",
            [AgentToolKeys.CancelScheduledJob] = "cancel scheduled job 11111111-1111-1111-1111-111111111111",
            [AgentToolKeys.GetCurrentDateTime] = "what time is it?"
        };
        foreach (var Tool in Tools.Where(Tool => Tool.Source == "Local"))
            JevToolArgumentCompiler.TryCompile(DirectMessages[Tool.Descriptor.Key], Tool, out _).Should().BeTrue(Tool.Function.Name);
        foreach (var Mode in new[] { JevRoutingMode.Shadow, JevRoutingMode.Suggest, JevRoutingMode.DirectReadOnly })
        foreach (var Tool in Tools)
        {
            var Decisions = new Mock<IToolDecisionClient>();
            Decisions.Setup(Client => Client.ChooseAsync(It.IsAny<ToolChoiceRequest>(), It.IsAny<JevRoutingSnapshot>(), It.IsAny<CancellationToken>()))
                .Returns((ToolChoiceRequest Request, JevRoutingSnapshot _, CancellationToken _) =>
                {
                    Request.Choices.Keys.Should().BeEquivalentTo(Tools.Select(Item => Item.Function.Name).Append("main_chat"));
                    string.Join(" ", Request.Choices.Values).Should().NotContain("private-remote-description");
                    return Task.FromResult(new ToolChoiceResult(Tool.Function.Name, 1, 1));
                });
            var Settings = Mock.Of<IJevRoutingSettings>(Value => Value.Current == new JevRoutingSnapshot(
                new JevRoutingSettings { Mode = Mode, AllowUserContent = true }, "synthetic-key"));
            var Result = await new JevRequestRouter(Settings, Decisions.Object).RouteAsync("synthetic request", Tools, default);
            Result.Reason.Should().Be(Mode == JevRoutingMode.Shadow ? "shadow" : "suggest");
            Result.SuggestedTool.Should().Be(Mode == JevRoutingMode.Shadow ? null : Tool.Function.Name);
            Result.Response.Should().BeNull();
        }
        Provider.Tools.Cast<StubFunction>().Should().OnlyContain(Tool => Tool.Invocations == 0);

        Store.Permissions[AgentToolKeys.ScheduleNotification] = false;
        Store.Permissions[AgentToolKeys.Tavily("tavily_search")] = false;
        var Restricted = await Binder.BindAsync(new("owner", AgentRoles.Owner, "subject"));
        Restricted.Select(Tool => Tool.Descriptor.Key).Should().NotContain([
            AgentToolKeys.ScheduleNotification, AgentToolKeys.Tavily("tavily_search")]);
        Restricted.Should().OnlyContain(Tool => JevToolRoutingCatalog.Description(Tool) != null);
    }

    [Fact]
    public async Task CatalogAndBoundFunctions_ShareIdentityDescriptionAndDefaults()
    {
        using var services = BuildServices();
        var access = services.GetRequiredService<ToolAccessService>();
        var binder = services.GetRequiredService<AgentToolBinder>();
        var catalog = await access.GetCatalogAsync();
        var tools = await binder.BindAsync(new("owner", AgentRoles.Owner, "owner"));

        tools.Select(Tool => Tool.Descriptor.Key).Should().BeEquivalentTo(catalog.Tools.Select(Tool => Tool.Key));
        tools.Should().HaveCount(11);
        foreach (var tool in tools)
        {
            tool.Function.Name.Should().Be(tool.Descriptor.Name);
            tool.Function.Description.Should().Be(tool.Descriptor.Description);
            var argumentNames = tool.Function.JsonSchema.GetProperty("properties").EnumerateObject().Select(Property => Property.Name);
            argumentNames.Should().NotContain(["profileId", "actorId", "role", "subjectProfileId"]);
        }
        catalog.Tools.Where(Tool => Tool.HasSideEffects).Select(Tool => Tool.Key).Should().BeEquivalentTo(
            [AgentToolKeys.PublishMobileNotification, AgentToolKeys.SyncWorkJournal, AgentToolKeys.ScheduleNotification, AgentToolKeys.ScheduleAgentTask,
                AgentToolKeys.UpdateScheduledJob, AgentToolKeys.CancelScheduledJob]);
    }

    [Fact]
    public async Task CoachBinding_ExposesOnlyAssignedCheckinSearchAndClock()
    {
        using var services = BuildServices();
        var tools = await services.GetRequiredService<AgentToolBinder>().BindAsync(new("coach", AgentRoles.Coach, "athlete"));

        tools.Select(Tool => Tool.Descriptor.Key).Should().BeEquivalentTo([AgentToolKeys.SearchCoachCheckins, AgentToolKeys.GetCurrentDateTime]);
    }

    [Fact]
    public async Task BoundNotification_CapturesEachServerSubject_AndIgnoresSuppliedProfile()
    {
        var bus = new Mock<IBus>();
        var notifications = new List<DevicePushNotificationRequested>();
        bus.Setup(Bus => Bus.Publish(It.IsAny<DevicePushNotificationRequested>(), It.IsAny<CancellationToken>()))
            .Callback<DevicePushNotificationRequested, CancellationToken>((Message, _) => notifications.Add(Message))
            .Returns(Task.CompletedTask);
        using var services = BuildServices(Bus: bus.Object);
        var binder = services.GetRequiredService<AgentToolBinder>();
        var first = (await binder.BindAsync(new("owner-a", AgentRoles.Owner, "athlete-a")))
            .Single(Tool => Tool.Descriptor.Key == AgentToolKeys.PublishMobileNotification).Function;
        var second = (await binder.BindAsync(new("owner-b", AgentRoles.Owner, "athlete-b")))
            .Single(Tool => Tool.Descriptor.Key == AgentToolKeys.PublishMobileNotification).Function;
        var arguments = new AIFunctionArguments { ["title"] = "Test", ["body"] = "Synthetic notification", ["profileId"] = "forged-profile" };

        await first.InvokeAsync(arguments);
        await second.InvokeAsync(arguments);

        notifications.Select(Message => Message.ProfileId).Should().Equal("athlete-a", "athlete-b");
        notifications.Should().OnlyContain(Message => Message.Title == "Test" && Message.Body == "Synthetic notification");
    }

    [Fact]
    public async Task RevokedPermission_IsCheckedAgainAtInvocation()
    {
        var store = new MutableStore();
        var bus = new Mock<IBus>(MockBehavior.Strict);
        using var services = BuildServices(Store: store, Bus: bus.Object);
        var tool = (await services.GetRequiredService<AgentToolBinder>().BindAsync(new("owner", AgentRoles.Owner, "owner")))
            .Single(Tool => Tool.Descriptor.Key == AgentToolKeys.PublishMobileNotification).Function;
        store.Permissions[AgentToolKeys.PublishMobileNotification] = false;

        await FluentActions.Awaiting(async () => await tool.InvokeAsync(new AIFunctionArguments { ["title"] = "Test", ["body"] = "Test" }))
            .Should().ThrowAsync<UnauthorizedAccessException>();
        bus.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task McpTools_StartDisabled_AndAvailabilityIsRecheckedAtInvocation()
    {
        var remote = new StubFunction("web_search");
        var provider = new MutableTavilyProvider { Tools = [remote] };
        var store = new MutableStore();
        using var services = BuildServices(provider, store);
        var binder = services.GetRequiredService<AgentToolBinder>();
        var access = new AgentAccessContext("owner", AgentRoles.Owner, "owner");
        (await binder.BindAsync(access)).Should().NotContain(Tool => Tool.Source == "TavilyMcp");
        var catalog = await services.GetRequiredService<ToolAccessService>().GetCatalogAsync();
        catalog.Tools.Single(Tool => Tool.Key == AgentToolKeys.Tavily("web_search")).HasSideEffects.Should().BeTrue();
        store.Permissions[AgentToolKeys.Tavily("web_search")] = true;
        var tool = (await binder.BindAsync(access)).Single(Tool => Tool.Source == "TavilyMcp").Function;
        provider.IsAvailable = false;

        await FluentActions.Awaiting(async () => await tool.InvokeAsync(new AIFunctionArguments()))
            .Should().ThrowAsync<UnauthorizedAccessException>();
        remote.Invocations.Should().Be(0);
        (await binder.BindAsync(access)).Should().NotContain(Tool => Tool.Source == "TavilyMcp");
    }

    [Fact]
    public void Registry_RejectsMcpNameCollisionWithLocalTool()
    {
        var registry = new AgentToolRegistry(new MutableTavilyProvider { Tools = [new StubFunction("search_work_journal")] });
        registry.Invoking(Registry => Registry.GetRegistrations()).Should().Throw<InvalidOperationException>().WithMessage("*unique*");
    }

    [Fact]
    public void Registry_RejectsDuplicateMcpRegistrations()
    {
        var registry = new AgentToolRegistry(new MutableTavilyProvider { Tools = [new StubFunction("search"), new StubFunction("search")] });
        registry.Invoking(Registry => Registry.GetRegistrations()).Should().Throw<InvalidOperationException>().WithMessage("*unique*");
    }

    [Fact]
    public async Task Binding_RequiresDefinedRoleAndServerContext()
    {
        using var services = BuildServices();
        var binder = services.GetRequiredService<AgentToolBinder>();
        await binder.Invoking(Binder => Binder.BindAsync(new("actor", "unknown", "subject"))).Should().ThrowAsync<InvalidOperationException>();
        await binder.Invoking(Binder => Binder.BindAsync(new("", AgentRoles.Owner, "subject"))).Should().ThrowAsync<InvalidOperationException>();
        await binder.Invoking(Binder => Binder.BindAsync(new("actor", AgentRoles.Owner, ""))).Should().ThrowAsync<InvalidOperationException>();
    }

    private static ServiceProvider BuildServices(ITavilyMcpToolProvider? Provider = null, MutableStore? Store = null, IBus? Bus = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Bus ?? Mock.Of<IBus>());
        services.AddSingleton(Options.Create(new SqlTransportOptions { ConnectionString = "Host=localhost;Database=test;Username=test;Password=test" }));
        services.AddSingleton(Options.Create(new AgentMemoryOptions()));
        services.AddSingleton(Options.Create(new CoachCheckinOptions()));
        services.AddSingleton(Mock.Of<IAgentEmbeddingService>());
        services.AddSingleton<SchedulingService>();
        services.AddSingleton<AgentEventService>();
        services.AddSingleton<WorkJournalService>();
        services.AddSingleton<CoachCheckinService>();
        services.AddSingleton<ITavilyMcpToolProvider>(Provider ?? new MutableTavilyProvider());
        services.AddSingleton<IToolAccessStore>(Store ?? new MutableStore());
        services.AddSingleton<IAgentToolRegistry, AgentToolRegistry>();
        services.AddSingleton<ToolAccessService>();
        services.AddSingleton<AgentToolBinder>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private sealed class MutableStore : IToolAccessStore
    {
        public Dictionary<string, bool> Permissions { get; } = [];
        public Task<IReadOnlyDictionary<string, bool>> GetRolePermissionsAsync(string Role, CancellationToken CancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, bool>>(Permissions);
        public Task SavePermissionsAsync(IReadOnlyList<ToolRolePermission> Permissions, string UpdatedBy, CancellationToken CancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MutableTavilyProvider : ITavilyMcpToolProvider
    {
        public IReadOnlyList<AIFunction> Tools { get; init; } = [];
        public bool IsAvailable { get; set; } = true;
        public string Status => "Test";
        public IReadOnlyList<AIFunction> GetTools() => Tools;
    }

    private sealed class StubFunction(string FunctionName) : AIFunction
    {
        public int Invocations { get; private set; }
        public override string Name => FunctionName;
        public override string Description => "private-remote-description";
        public override JsonElement JsonSchema => JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments Arguments, CancellationToken CancellationToken)
        {
            Invocations++;
            return ValueTask.FromResult<object?>("Synthetic result");
        }
    }
}
