# Runtime agent skills

The API uses Microsoft Agent Framework skills for product-domain procedures that are useful only on matching turns. Skills live under `PersonalAgent.Api/Skills/<skill-name>/SKILL.md` and are copied to the API output and publish directories.

## Current skill

`coach-answer-grounding` tells the agent how to search coaching check-ins, select recency, attribute transcript evidence, and preserve evidence links. It contains instructions only. Coaching data still comes from the server-scoped `search_coach_checkins` tool.

## Add or change a skill

1. Use a lowercase hyphenated directory name matching the `name` in `SKILL.md` frontmatter.
2. Give the description a narrow trigger boundary; only the name and description are advertised on every turn.
3. Keep conditional operating guidance in the body. Add `references/` only when details are not needed on every matching use.
4. Do not add executable scripts. A script runner is intentionally not configured; introducing one requires a security and operations decision.
5. Keep identity, authorization, validation, persistence, and side effects in services registered through `AgentToolRegistry`. A skill can explain when to call a tool but cannot grant access to it.
6. Add a test that loads meaningful instructions through `AgentSkillsProvider`; do not assert only the raw file text.

## Approval and trust boundary

`PersonalAgentSkills` disables approval for `load_skill` and `read_skill_resource` because deployment contains only reviewed repository content. Do not point this provider at user-writable or remote directories. External or per-user skill sources need isolation and explicit approval policy before use.

## Verification

Run the focused packaging/provider test:

```powershell
dotnet test PersonalAgent.Api.Tests/PersonalAgent.Api.Tests.csproj --filter FullyQualifiedName~AgentSkillsTests
```

For coaching instruction changes, also run the deterministic coaching retrieval tests and the opt-in model comparison described in [coaching retrieval evaluation](coach-retrieval-evaluation.md). Provider discovery proves that a skill can load; it does not prove that a model selects or follows it reliably.
