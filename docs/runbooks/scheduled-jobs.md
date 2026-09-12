# Scheduled agent jobs

Open **Scheduled jobs** from the Web menu (`/jobs`). Ask the assistant in Chat to schedule work for later, or use the signed `POST /api/schedule/agent-tasks` endpoint. The returned ID is the durable application `TaskId`. Chat-created jobs retain the originating conversation.

The page shows scheduler, subject, due time in the browser's timezone, status, result and execution attempts. Filter by subject/status and page through older jobs. It refreshes every 15 seconds. Owners see jobs for their own subject, including jobs created by a coach; coaches see only their own jobs for currently assigned subjects. Conversation links appear only for the conversation's actor. Result conversations are read-only.

## Permissions

Scheduling requires a signed actor, current subject access and the `Local:schedule_agent_task` permission. Coaches cannot schedule by default. The API reads active `Authentication:Schemes:GitHub:AllowedUsers` and `Authentication:Schemes:Google:AllowedEmails` rows from Shared/Web database configuration for every background authorization check; Web overrides Shared. Encrypted values are supported. Existing environment-only sign-in access does not grant background scheduling: put the intended allowlist in the existing database settings editor. No new environment variable or credential is required.

Creation, execution and scheduled tool invocation check current policy. Assignment/tool revocation takes effect on the next check. A denied run is Blocked, with no further unauthorized action. Actions completed before revocation remain completed. Web sign-in configuration still has its existing startup lifecycle; this change makes background job checks live, not all authentication.

## Recovery and cancellation

| State | Meaning / action |
| --- | --- |
| Scheduled | Awaiting due time. Can be cancelled. |
| Retrying | Could not start safely; reconciliation retries, up to five attempts. Can be cancelled when no execution owns the job lock. |
| Running | Agent execution has started; cancellation is unavailable. |
| Completed | Result and completion notification intent committed. Duplicate delivery returns this result. |
| Blocked | Missing/revoked authorization or legacy schedule. Inspect the explanation; create new authorized work if appropriate. |
| NeedsReview | Interrupted after execution began without a saved response. Inspect external effects before scheduling any replacement. Automatic replay and manual retry are intentionally unavailable. |
| Failed | Five attempts could not start execution. Resolve the cause, then create a new job. |
| Cancelled | Pending work was cancelled; queued deliveries cannot execute it. |

API owns `agent_memory.scheduled_jobs` and `scheduled_job_attempts` (under the configured AgentMemory schema). A session advisory lock serializes execution/cancellation across API instances without a long database transaction around model calls. State changes use short transactions. Creation and terminal notification intents share the existing domain outbox, drained by Worker. API reconciles due Scheduled/Retrying/Running rows every 15 seconds, with at most one recovery enqueue per minute per job. MassTransit duplicate deliveries are expected.

After an interrupted run, API rechecks permissions and recovers a saved assistant response if present; otherwise it records NeedsReview without repeating the agent turn. This conservative policy is not a general external-tool idempotency guarantee or resumable framework execution. A result can exist even when post-response memory indexing failed; recovery retains the saved response.

Existing queued schedules have no trustworthy originating actor. On delivery they become Blocked legacy records, show an unknown scheduler and never execute or send a completion notification. Their claimed subject is retained for owner inspection. They do not appear before delivery because no application record previously existed. Roll out API and Worker together: an old Worker still contains the removed Owner execution behavior.

This first screen covers scheduled **agent tasks**. Plain scheduled push reminders, transcription jobs, recurring schedules, editing, pause/resume, manual retries and human-approval continuations remain separate work. Push delivery retains the existing transport semantics; atomic notification intent is not an exactly-once device-delivery claim.

## Local verification

Start the synthetic stack using [the demo runbook](synthetic-demo.md), then run:

```powershell
pwsh -NoProfile -File scripts/tests/Test-ScheduledJobs.ps1
```

The script targets only the fixed synthetic endpoint, restarts its Worker, creates sample jobs, temporarily revokes/restores the synthetic scheduling permission, and checks completion, duplicate delivery, read-only result history, isolation, cancellation and denial. It retains sample jobs for dashboard inspection. The synthetic sign-in page is at `http://127.0.0.1:15000/login`.
