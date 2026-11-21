# Draft PR: permission-scoped coaching site actions

Local draft description only. No PR is opened or pushed by this implementation task.

## Summary

- Add reusable settings and coaching-upload action services behind existing authentication/authorization and persistence.
- Let an authorized agent propose exact non-secret coaching participant-name changes; require a separate authenticated exact-change confirmation before saving. No model-callable commit or operational/security mutation.
- Discover exact manual setting inputs without returning values/secrets, including revision-backed dedicated forms.
- Accept owned `.m4a` uploads from Web chat through the existing coaching pipeline, preserving validation, duplicate suppression, durable enqueue and actual status. Ambiguous context requires a choice.
- Preserve manual-only speaker, security, credential, account access, telemetry, backup and sandbox/coding administration safeguards.

## Coverage and limitations

See [the inventory](../runbooks/site-actions.md#manual-action-inventory-and-coverage) and [decision 0053](../decisions/0053-permission-scoped-site-actions.md). Channel mapping is fixed code, not a setting; retrieval options are existing per-query arguments. Text imports and sandbox-reference-to-coaching ingestion are not supported. Review links are process-local, bounded and expire after 15 minutes; API instance affinity is needed across replicas. Native Android gets the same backend tools/links, but no new native upload UI in this slice.

## Validation evidence

Validated on 2026-11-21 with normal NuGet restore:

| Command | Result |
| --- | --- |
| `dotnet test PersonalAgent.Api.Tests/PersonalAgent.Api.Tests.csproj --filter "FullyQualifiedName~SiteActionsTests\|FullyQualifiedName~AgentToolRegistryTests\|FullyQualifiedName~IntegrationSettingsEndpointsTests\|FullyQualifiedName~PersonalAgentEndpointsTests"` | 75 passed, 0 failed |
| `dotnet test PersonalAgent.Web.Tests/PersonalAgent.Web.Tests.csproj` | 98 passed, 0 failed |
| `dotnet build PersonalAgent.Mobile/PersonalAgent.Mobile.csproj` | Succeeded; 0 warnings, 0 errors |
| `node --check PersonalAgent.Web/wwwroot/js/app.js` | Passed |
| `git diff --check` | Passed |
| `dotnet test PersonalAgent.Api.Tests/PersonalAgent.Api.Tests.csproj` | Not green: Docker/PostgreSQL fixtures cannot connect to `unix:///var/run/docker.sock` in this environment. Docker-backed acceptance remains blocked, including the added `CoachingUploadDatabaseTests` |

Focused tests cover permitted/denied roles, deployment administrators, actor/subject isolation, secret/operational setting rejection, absent explicit confirmation, untrusted-content approval attempts, exact immutable payload confirmation, stale/replayed/failed saves, field links, owned upload submission, foreign-profile and text rejection, duplicate/processing status, cancelled/ambiguous upload intent and storage failure propagation. The added database tests cover actual concurrent deduplication/outbox dispatch intent, foreign IDs and transaction rollback, but their behavior has not been validated here because Docker is unavailable.

Hosted/provider acceptance and real browser anchor/focus behavior are pending. No credentials, deployment settings, `.github` files or remote branches are changed. The native Android build is validation, not a new native upload UI or deployment.
