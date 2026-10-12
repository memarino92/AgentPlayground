# 0045: Bounded Railway runtime log inspection

- Status: Proposed for maintainer review; implementation present locally, live acceptance pending
- Recorded: 2026-10-12
- Evidence: `RailwayLogTool`, `DatabaseRailwayLogSettings`, `AgentToolRegistry`, official Railway sources below

## Context

The assistant needs recent operational evidence without giving it a shell, arbitrary cloud targets or resource-management rights. Existing Railway adapters execute disposable sandbox jobs; inspection found no prior log-tool implementation. The executable registry and invocation-time permission wrapper already provide signed actor/role enforcement. Configuration is database-first and the deployment-administrator editor masks encrypted secrets.

## Decision

Use the official GraphQL public API through a fixed HTTPS destination, with redirects disabled. Only three fixed queries exist: project-token identity, scoped recent deployments, and bounded runtime logs. Require Owner role plus an explicit role-matrix grant (disabled by default). Project/environment IDs and service aliases/IDs are administrator-configured; the model supplies aliases and a deployment ID only after scope checking against at most 20 recent scoped deployments.

Support only environment-scoped **project tokens**, sent as `Project-Access-Token`, and verify their returned identity against settings. Read exact active `Api` configuration rows on every invocation, decrypt the token, validate and fail closed; no stale credential fallback. Settings removal, disabled integration and token rotation apply on subsequent calls without restart. In-flight calls keep their original snapshot.

## Alternatives

- Railway CLI: official bounded `--lines` exists, but introduces a binary/version dependency and process/argument management. Fixed HTTP queries fit the existing API adapter convention and avoid shell execution entirely.
- Account/workspace tokens: wider scope than necessary; deliberately unsupported here.
- Arbitrary deployment lookup: avoided because the documented scoped deployment list proves membership without assuming undocumented identity fields.
- Streaming/build/HTTP logs: omitted from this runtime-diagnostics slice; the API supports separate build/HTTP queries but their semantics require separate bounded contracts.

## Consequences

One configured project/environment and up to 20 services per platform deployment. Explicit deployment selection is limited to the 20 most recent deployments; newest does not necessarily mean latest successful/running. Search/severity are local filters over a bounded provider snapshot, not exhaustive provider searches. Limits are 200 lines, a 24-hour window, 1 MiB per response, approximately 32 KiB serialized output and a 15-second overall deadline. No automatic retries/pagination.

Log data is labeled untrusted and redacted using the existing credential masker, exact Railway-token replacement and connection-URL masking. Arbitrary private content and unlabeled secrets cannot be reliably recognized. Returned evidence can reach the model provider and persisted conversation. No additional log store is created.

## Source evidence and verification

Official documentation fetched on 2026-10-12:

- [Public API](https://docs.railway.com/reference/public-api): endpoint, project token environment scope/header, token creation, `projectToken` identity, GraphQL errors and rate limits.
- [Manage deployments](https://docs.railway.com/integrations/api/manage-deployments): `deployments(input: DeploymentListInput!, first: Int)` with project/service/environment scope; `deploymentLogs(deploymentId: String!, limit: Int)`, timestamp/message/severity and optional `startDate`/`endDate`/`filter` arguments. These are documented API fields, not an assumed log endpoint.
- [Logs](https://docs.railway.com/guides/logs): normalization to `debug`/`info`/`warn`/`error`, dropped logs at throughput limits and plan-dependent retention. The adapter normalizes `warn` to `warning` and accepts both spellings.

Fake-HTTP tests verify those request shapes and security boundaries. They do not prove token permissions or live account behavior. Maintainer review and a read-only live smoke call with operator-provided credentials remain acceptance criteria in the [roadmap](../plans/roadmap.md). See the [setup runbook](../runbooks/railway-logs.md) for limits and test evidence.
