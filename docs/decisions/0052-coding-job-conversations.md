# 0052: Coding job conversations and pull request follow-ups

- Status: Accepted for local implementation, live acceptance pending
- Recorded: 2026-11-20
- Decision date: 2026-11-20
- Evidence: Owner requested chat delivery, mobile questions, same-PR revisions, and OpenCode 2.

## Context

The coding worker starts a separate OpenCode process in a disposable Railway sandbox. Its dashboard historically held the only durable job outcome. That made interrupting and revising work difficult when the originating chat was closed. The existing GitHub App already owns isolated draft branches and can publish regular text changes with a test gate.

## Decision

Persist the originating conversation identity on each chat-started job. Reconcile terminal jobs and questions into the existing transcript under its sequence lock. Use stable event IDs to avoid duplicate transcript entries across retries. Registered Android devices receive an alert for open questions. The web and Android expose outstanding questions, and the original chat can relay an explicit answer using its existing coding inspection tool.

Pin OpenCode 2.3.2 and expose a local MCP tool inside the coding image. The tool uses the existing job capability to submit one question at a time and retrieve its answer. Questions and answers are owner- and job-scoped. The live process waits within its existing runtime budget.

An explicit CI or review follow-up starts a fresh job against the existing PR's current head. The publisher rechecks repository, branch, PR marker, and review policy and adds a fast-forward commit after the test gate. Existing jobs can be selected after rollout. GitHub feedback and check diagnostics are inputs to the coding process. They carry no authority to call platform tools or publish changes.

## Alternatives and consequences

A persistent Cloud Agent could preserve a checkout and conversation across a long wait. The existing sandbox already preserves its process while the job runs, and users can continue against the same PR after it ends. Keeping this bounded runner avoids changing the publication and isolation model. Unanswered questions expire with the job and are removed from the outstanding list.

## Delivery and verification

The local image proof uses synthetic model responses for both supported provider protocols and exercises asking a question, receiving a reply, editing a file, and exporting its diff. The [coding runbook](../runbooks/platform-coding.md) describes the live acceptance run and checkpoint rollout.
