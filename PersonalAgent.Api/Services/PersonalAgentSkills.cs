using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace PersonalAgent.Api.Services;

internal static class PersonalAgentSkills
{
    internal const string CoachAnswerGrounding = "coach-answer-grounding";

    public static AgentSkillsProvider Create(ILoggerFactory LoggerFactory) => new(
        Path.Combine(AppContext.BaseDirectory, "Skills"),
        options: new AgentSkillsProviderOptions
        {
            DisableLoadSkillApproval = true,
            DisableReadSkillResourceApproval = true
        },
        loggerFactory: LoggerFactory);
}
