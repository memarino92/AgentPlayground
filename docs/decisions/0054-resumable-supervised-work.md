# 0054: Resumable coding jobs and request-level supervision

- Status: Accepted for local implementation. Railway acceptance pending.
- Recorded: 2026-11-21
- Decision date: 2026-11-21
- Evidence: The production coding rerun in PR #113 used about 26 minutes. An earlier attempt hit its 20-minute cap and retained no diff. The owner requested resumable coding and supervision across mixed requests.

## Context

A coding job has a pinned base, bounded model budget, and disposable Railway sandbox. Its existing publication gate requires a trusted file export and passing tests. When the Worker stops the runner at the hard deadline, the sandbox can be cleaned up before an export is available. The chat agent also needs one durable run for a request that combines public research, code and a report.

## Decision

Before a coding sandbox reaches its hard deadline, stop the candidate process and export its bounded text changes and a progress note from a separate offline container. Save the checkpoint under the same job ID. Cleanup must acknowledge the old sandbox before the coordinator queues a fresh one. Restore the saved changes onto a checkout of the pinned base. A fresh OpenCode session receives the saved context and any prior owner answers. Validation can restart separately when the Worker saved evidence that coding had completed.

New jobs allow up to three sandbox attempts within one overall deadline and one shared provider request budget. Each attempt gets its own capability. Publication still requires a passing validation gate and the existing repository and review checks.

The chat agent plans across domains and submits one owner-authorized, one-time recipe. The existing automation saga persists its steps and collects their outcomes. A mixed run can use public research, a resumable coding child job and existing authorized recipe actions, such as an analysis program or saved report. Independent public research questions can also run in parallel directly from chat.

For difficult coding steps the OpenCode supervisor uses independent read-only specialists. The supervisor integrates their recommendations as the sole writer and runs validation. Research and subagent output can inform the work. Only the owner instruction and the existing tool grants authorize what the workflow may do.

## Alternatives

A persistent developer VM would retain the running CLI, package cache and tool processes. Disposable attempts keep the existing isolation and use bounded database artifacts. Parallel agents writing a shared checkout could overwrite each other's edits. A sole writer gives the supervisor one coherent candidate to validate and publish.

## Consequences

The checkpoint restores source changes and bounded notes. A fresh sandbox restores dependencies again. Unexpected loss of a sandbox or Worker can still lose work performed since the last export. A job awaiting the owner's answer can resume after cleanup once the answer arrives. Research uses the existing permissioned search and model providers.

## Delivery and verification

See the [coding operations runbook](../runbooks/platform-coding.md), [automation runbook](../runbooks/automations.md) and [roadmap](../plans/roadmap.md). Live acceptance requires a new Railway coding image and a mixed run that preserves a known file edit across a controlled timeout, passes validation and makes the research and draft PR visible from the parent run. [OpenCode agent configuration](https://opencode.ai/docs/agents/)
