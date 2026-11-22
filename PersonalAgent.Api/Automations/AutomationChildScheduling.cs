using PersonalAgent.Contracts.Automations;

namespace PersonalAgent.Api.Automations;

// Only research/coding children overlap. Other actions are source-order barriers, retaining
// the existing effect ordering and program execution contracts.
internal static class AutomationChildScheduling
{
    internal static bool IsChild(AutomationStep Step) => Step.Action is "public_research" or "coding_job";
    internal static bool Terminal(AutomationStepExecution Step) => Step.Status is "Completed" or "Skipped";

    internal static IEnumerable<string> Dependencies(AutomationRecipe Recipe, int Index)
    {
        var Step = Recipe.Steps[Index];
        var Prior = Recipe.Steps.Take(Index);
        if (!Recipe.ParallelChildren || !IsChild(Step)) return Prior.Select(S => S.Id);
        var Required = new HashSet<string>(Step.DependsOn ?? []);
        // A non-child is a barrier, including all its transitive predecessors.
        foreach (var Earlier in Prior.Where(S => !IsChild(S))) Required.Add(Earlier.Id);
        if (Step.When is { } Condition) Required.Add(Condition.Step);
        if (Step.Action == "coding_job" && Step.Arguments.TryGetProperty("evidenceSteps", out var Evidence))
            foreach (var Id in Evidence.EnumerateArray()) Required.Add(Id.GetString()!);
        return Required;
    }

    internal static bool Ready(AutomationRecipe Recipe, int Index, IReadOnlyList<AutomationStepExecution> Steps) =>
        Dependencies(Recipe, Index).All(Id => Steps.Any(S => S.StepId == Id && Terminal(S)));
}
