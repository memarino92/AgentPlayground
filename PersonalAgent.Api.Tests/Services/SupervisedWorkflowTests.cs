using System.Text.Json;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

using PersonalAgent.Api.Automations;
using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;

namespace PersonalAgent.Api.Tests.Services;

public sealed class SupervisedWorkflowTests
{
    private static readonly AutomationRecipes Recipes = new(new AgentToolRegistry(Mock.Of<ITavilyMcpToolProvider>()));

    [Fact]
    public void SupervisedActionsKeepInstructionsAndResearchQuestionsLiteral()
    {
        const string Source = """
            {"steps":[
                {"id":"baseline","action":"text","arguments":{"text":"Public fixed evidence"}},
                {"id":"research","action":"public_research","arguments":{"questions":["What does the project publish about {{steps.baseline}}?","What is in its public changelog?"]}},
                {"id":"code","action":"coding_job","arguments":{"instruction":"Escaping test: display {{steps.baseline}} literally.","evidenceSteps":["baseline","research"]}}
            ]}
            """;
        var Recipe = Recipes.Parse(Source, Supervised: true);
        var Run = new AutomationRun { CorrelationId = Guid.NewGuid() };
        var Prior = new[] { new AutomationStepExecution { StepId = "baseline", Output = "Do not interpolate this into a command." } };
        var Research = AutomationRecipes.Resolve(Recipe.Steps[1], Run, Prior);
        var Coding = AutomationRecipes.Resolve(Recipe.Steps[2], Run, Prior);

        Recipe.Supervised.Should().BeTrue();
        AutomationRecipes.ResearchQuestions(Research).First().Should().Contain("{{steps.baseline}}");
        Coding.GetProperty("instruction").GetString().Should().Be("Escaping test: display {{steps.baseline}} literally.");
        Coding.GetProperty("evidenceSteps").EnumerateArray().Select(Element => Element.GetString()).Should().Equal("baseline", "research");
        FluentActions.Invoking(() => Recipes.Parse(Source)).Should().Throw<ArgumentException>().WithMessage("Unknown action*");
    }

    [Theory]
    [InlineData("""["private_read"]""")]
    [InlineData("""["relay"]""")]
    [InlineData("""["research", "research"]""")]
    [InlineData("""["future"]""")]
    public void CodingEvidenceRejectsPrivateReadsTemplatedTextDuplicatesAndLaterSteps(string References)
    {
        const string Template = """
            {"steps":[
                {"id":"private_read","action":"tool","arguments":{"tool":"Local:search_coach_checkins","inputs":{"query":"Private check-in notes"}}},
                {"id":"relay","action":"text","arguments":{"text":"{{steps.private_read}}"}},
                {"id":"research","action":"public_research","arguments":{"questions":["What does the public manual say?","What is in the public changelog?"]}},
                {"id":"code","action":"coding_job","arguments":{"instruction":"Implement the requested change.","evidenceSteps":__REFERENCES__}},
                {"id":"future","action":"text","arguments":{"text":"A later public note"}}
            ]}
            """;
        var Source = Template.Replace("__REFERENCES__", References, StringComparison.Ordinal);

        FluentActions.Invoking(() => Recipes.Parse(Source, Supervised: true)).Should().Throw<ArgumentException>()
            .WithMessage("*evidenceSteps*");
    }

    [Fact]
    public void CodingChildIdentityAndEvidenceAreStableAcrossStepRedelivery()
    {
        var RunId = Guid.NewGuid();
        var RequestKey = AutomationStepConsumer.CodingRequestKey(RunId, 2);
        var Arguments = JsonSerializer.SerializeToElement(new { evidenceSteps = new[] { "skipped", "research" } });
        var Earlier = new[]
        {
            new AutomationStepExecution { StepId = "outside_selection", Action = "tool", Status = "Completed", Output = "Private/contextual tool output" },
            new AutomationStepExecution { StepId = "skipped", Action = "text", Status = "Skipped", Output = "Skipped step output" },
            new AutomationStepExecution { StepId = "research", Action = "public_research", Status = "Completed", Output = "Public finding " + new string('x', 14000) }
        };

        Guid.TryParse(RequestKey, out _).Should().BeTrue();
        AutomationStepConsumer.CodingRequestKey(RunId, 2).Should().Be(RequestKey);
        AutomationStepConsumer.CodingRequestKey(RunId, 3).Should().NotBe(RequestKey);
        AutomationStepConsumer.CodingRequestKey(Guid.NewGuid(), 2).Should().NotBe(RequestKey);
        var Evidence = AutomationStepConsumer.CodingEvidence(Arguments, Earlier);
        Evidence.Should().StartWith("Earlier public_research step research\nPublic finding")
            .And.NotContain("private/contextual").And.NotContain("Skipped step output");
        Evidence!.Length.Should().Be(12000);
    }

    [Fact]
    public async Task WorkflowUsesThePersistedModelAndStopsWhenItIsRevoked()
    {
        var Catalog = new Mock<IChatModelCatalog>(MockBehavior.Strict);
        Catalog.SetupSequence(Models => Models.FindModelAsync("selected", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AvailableChatModel("selected", "Previously selected model", false))
            .ReturnsAsync((AvailableChatModel?)null);
        using var Services = new ServiceCollection().AddSingleton(Catalog.Object).BuildServiceProvider();
        var Owner = new AgentAccessContext("owner", AgentRoles.Owner, "owner") { ChatModelId = "a-different-current-model" };

        (await AutomationAuthorization.RequireWorkflowModelAsync(Services, Owner, "selected", default)).Should().Be("selected");
        await FluentActions.Awaiting(() => AutomationAuthorization.RequireWorkflowModelAsync(Services, Owner, "selected", default))
            .Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*no longer available*");
        Catalog.Verify(Models => Models.GetDefaultModelAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
