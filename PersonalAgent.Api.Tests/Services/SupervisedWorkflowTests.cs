using System.Text.Json;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

using PersonalAgent.Api.Automations;
using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;
using PersonalAgent.Contracts.Automations;

namespace PersonalAgent.Api.Tests.Services;

public sealed class SupervisedWorkflowTests
{
    private static readonly AutomationRecipes Recipes = new(new AgentToolRegistry(Mock.Of<ITavilyMcpToolProvider>()));

    [Fact]
    public void ConditionsAndNonChildBarriersWaitForTerminalPredecessors()
    {
        var Recipe = Recipes.Parse("""
            {"parallelChildren":true,"steps":[
              {"id":"first","action":"public_research","arguments":{"questions":["Public manual?","Public changelog?"]}},
              {"id":"conditional","action":"public_research","when":{"step":"first","equals":"expected"},"arguments":{"questions":["Another manual?","Another changelog?"]}},
              {"id":"barrier","action":"text","arguments":{"text":"{{steps.conditional}}"}},
              {"id":"code","action":"coding_job","arguments":{"instruction":"Implement this."}}
            ]}
            """, Supervised: true);
        var Steps = Recipe.Steps.Select((S, I) => new AutomationStepExecution { Index = I, StepId = S.Id, Action = S.Action }).ToArray();
        AutomationChildScheduling.Ready(Recipe, 1, Steps).Should().BeFalse();
        Steps[0].Status = "Completed";
        AutomationChildScheduling.Ready(Recipe, 1, Steps).Should().BeTrue();
        AutomationChildScheduling.Ready(Recipe, 2, Steps).Should().BeFalse();
        Steps[1].Status = "Skipped";
        AutomationChildScheduling.Ready(Recipe, 2, Steps).Should().BeTrue();
        AutomationChildScheduling.Ready(Recipe, 3, Steps).Should().BeFalse();
        Steps[2].Status = "Completed";
        AutomationChildScheduling.Ready(Recipe, 3, Steps).Should().BeTrue();
    }

    [Fact]
    public void OldSavedMessagesAndRecipesDefaultToSerialGenerationZero()
    {
        var Run = Guid.NewGuid();
        var Execute = JsonSerializer.Deserialize<ExecuteAutomationStep>($$"""{"RunId":"{{Run}}","StepIndex":1}""")!;
        var Complete = JsonSerializer.Deserialize<AutomationStepCompleted>($$"""{"RunId":"{{Run}}","StepIndex":1,"Output":"old"}""")!;
        var Failed = JsonSerializer.Deserialize<AutomationStepFailed>($$"""{"RunId":"{{Run}}","StepIndex":1,"Error":"old"}""")!;
        Execute.Attempt.Should().Be(0);
        Complete.Attempt.Should().Be(0);
        Failed.Attempt.Should().Be(0);
        Recipes.Parse("""{"steps":[{"id":"old","action":"text","arguments":{"text":"literal"}}]}""").ParallelChildren.Should().BeFalse();
    }

    [Fact]
    public void ParallelChildrenOverlapOnlyWhenIndependentAndLegacyRecipesRemainSerial()
    {
        const string Source = """
            {"parallelChildren":true,"steps":[
              {"id":"research","action":"public_research","arguments":{"questions":["First public question?","Second public question?"]}},
              {"id":"code","action":"coding_job","arguments":{"instruction":"Implement this change."}},
              {"id":"report","action":"save_report","arguments":{"title":"Result","content":"{{steps.research}} {{steps.code}}"}}
            ]}
            """;
        var Recipe = Recipes.Parse(Source, Supervised: true);
        var Steps = Recipe.Steps.Select((Step, Index) => new AutomationStepExecution
            { Index = Index, StepId = Step.Id, Action = Step.Action }).ToArray();
        AutomationChildScheduling.Ready(Recipe, 0, Steps).Should().BeTrue();
        AutomationChildScheduling.Ready(Recipe, 1, Steps).Should().BeTrue();
        AutomationChildScheduling.Ready(Recipe, 2, Steps).Should().BeFalse();
        var Legacy = Recipes.Parse(Source.Replace("\"parallelChildren\":true,", ""), Supervised: true);
        AutomationChildScheduling.Ready(Legacy, 1, Steps).Should().BeFalse();
        var Evidence = Recipe.Steps[1] with { Arguments = JsonSerializer.SerializeToElement(new
            { instruction = "Implement this change.", evidenceSteps = new[] { "research" } }) };
        var Dependent = Recipe with { Steps = [Recipe.Steps[0], Evidence, Recipe.Steps[2]] };
        AutomationChildScheduling.Ready(Dependent, 1, Steps).Should().BeFalse();
        var Explicit = Recipe with { Steps = [Recipe.Steps[0], Recipe.Steps[1] with { DependsOn = ["research"] }, Recipe.Steps[2]] };
        AutomationChildScheduling.Ready(Explicit, 1, Steps).Should().BeFalse();
        Steps[0].Status = "Completed";
        AutomationChildScheduling.Ready(Dependent, 1, Steps).Should().BeTrue();
        AutomationChildScheduling.Ready(Explicit, 1, Steps).Should().BeTrue();
        AutomationChildScheduling.Ready(Recipe, 2, Steps).Should().BeFalse();
        Steps[1].Status = "Completed";
        AutomationChildScheduling.Ready(Recipe, 2, Steps).Should().BeTrue();
    }

    [Theory]
    [InlineData("\"parallelChildren\":true,", "[\"future\"]")]
    [InlineData("\"parallelChildren\":true,", "[\"first\",\"first\"]")]
    [InlineData("", "[\"first\"]")]
    public void DependenciesRejectFutureCyclesDuplicatesAndNonOptedInRecipes(string Option, string DependsOn)
    {
        var Source = """
            { OPTION"steps":[{"id":"first","action":"text","arguments":{"text":"literal"}},
            {"id":"next","action":"text","dependsOn":DEPENDENCIES,"arguments":{"text":"literal"}}]}
            """.Replace("OPTION", Option).Replace("DEPENDENCIES", DependsOn);
        FluentActions.Invoking(() => Recipes.Parse(Source, Supervised: true)).Should().Throw<ArgumentException>().WithMessage("*dependsOn*");
    }

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
