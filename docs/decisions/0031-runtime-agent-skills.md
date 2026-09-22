# 0031: Runtime Agent Framework skills

- Status: Accepted; first skill implemented
- Recorded: 2026-09-21
- Decision date: 2026-09-21
- Evidence: `PersonalAgent.Api/Skills/coach-answer-grounding/SKILL.md`, `PersonalAgentSkills`, `AgentChatService`, and `AgentSkillsTests`
- Supersedes / superseded by: extends [0004](0004-agent-service-boundary.md) and does not change [0006](0006-personal-assistant-tooling.md)

## Context

The API agent accumulated detailed coaching retrieval and citation rules in its universal system prompt even though those rules apply only to coaching questions. Microsoft Agent Framework 1.13.0 supports progressively disclosed Agent Skills through `AgentSkillsProvider`. The application already has an authorized tool registry, so skills must not become a second execution or authorization path.

## Decision

Use repository-owned, file-based Agent Skills for conditional product-domain instructions and trusted read-only resources. Package them with the API and expose them through one cached `AgentSkillsProvider`. Automatically allow the provider's read-only `load_skill` and `read_skill_resource` operations because the packaged source is reviewed application content.

Keep business operations in the existing tool registry. Skills may instruct the model to call an authorized tool, but they do not grant access, bind actor or subject identity, or replace execution-time permission checks. Do not configure a file skill script runner in this first slice.

The first skill, `coach-answer-grounding`, owns the procedure for querying coaching calls, selecting recency, attributing coach utterances, and preserving evidence links. General assistant, scheduling, journal, and presentation rules remain in the base prompt until a measured use case justifies another skill.

## Alternatives

Leaving all rules in the universal prompt avoids another framework component but spends context on unrelated turns and makes domain procedures harder to package and test independently. Code-defined skills would avoid content deployment but make instruction editing less direct and abandon the portable `SKILL.md` format. Skill scripts were rejected for this slice because the required operations already exist as authorized typed tools and an additional execution path would increase risk without adding capability.

## Consequences

The model sees only each skill's name and description until it calls `load_skill`, reducing unconditional prompt content. Skill selection adds a model tool round trip to matching requests and therefore needs evaluation against the existing coaching baseline. A malformed or missing packaged skill can remove important model guidance even though server authorization remains intact.

Bundled skill content is trusted prompt input, not trusted authority. Future user-authored, database-backed, or remote skills require a separate trust, filtering, caching-isolation, and approval decision. Executable skill scripts require an explicit sandbox and audit design.

## Delivery and verification

The API packages `Skills/**`, registers a singleton provider, and attaches it to each session agent as an AI context provider. Read-only skill operations bypass approval; no script runner is present. A focused test verifies file discovery, prompt advertisement, and full instruction loading. The existing coaching retrieval/model evaluation remains the quality gate for behavior changes; run it before claiming parity or improvement in live answers. See [agent skills](../runbooks/agent-skills.md).
