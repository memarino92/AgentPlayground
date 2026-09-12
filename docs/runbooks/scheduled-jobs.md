# Scheduled jobs

Open **Scheduled jobs** from the Web menu (`/jobs`). Ask the assistant in Chat to schedule work for later, or use the signed `POST /api/schedule/agent-tasks` and `POST /api/schedule/notifications` endpoints. The returned ID is the durable application `TaskId`. Chat-created jobs retain the originating conversation.

The page labels each job **Agent task** or **Notification** and shows scheduler, subject, due time in the browser's timezone, status, result and execution attempts. Filter by subject/status and page through older jobs. It refreshes every 15 seconds. Owners see jobs for their own subject, including jobs created by a coach; coaches see only their own jobs for currently assigned subjects. Conversation links appear only for the conversation's actor. Result conversations are read-only.

## Permissions

Scheduling requires a signed actor, current subject access and the matching `Local:schedule_agent_task` or `Local:schedule_notification` permission. Coaches cannot schedule by default. The API reads active `Authentication:Schemes:GitHub:AllowedUsers` and `Authentication:Schemes:Google:AllowedEmails` rows from Shared/Web database configuration for every background authorization check; Web overrides Shared. Encrypted values are supported. Existing environment-only sign-in access does not grant background scheduling: put the intended allowlist in the existing database settings editor. No new environment variable or credential is required.

Creation, execution and scheduled tool invocation check current policy. Assignment/tool revocation takes effect on the next check. A denied run is Blocked, with no further unauthorized action. Actions completed before revocation remain completed. Web sign-in configuration still has its existing startup lifecycle; this change makes background job checks live, not all authentication.

## Recovery and cancellation

| State | Meaning / action |
| --- | --- |
| Scheduled | Awaiting due time. Can be cancelled. |
| Retrying | Could not start safely; reconciliation retries, up to five attempts. Can be cancelled when no execution owns the job lock. |
| Running | Agent execution or notification delivery has started; cancellation is unavailable. |
| Completed | Agent result and optional completion notification intent committed, or all notification sends accepted by the push provider. This does not confirm device receipt. Duplicate delivery returns the stored result. |
| Blocked | Missing/revoked authorization or legacy schedule. Inspect the explanation; create new authorized work if appropriate. |
| NeedsReview | Interrupted after execution began without a saved response. Inspect external effects before scheduling any replacement. Automatic replay and manual retry are intentionally unavailable. |
| Failed | Execution could not start after five attempts, or notification delivery was unavailable (for example, push disabled or no registered devices). Inspect the outcome before creating a replacement. |
| Cancelled | Pending work was cancelled; queued deliveries cannot execute it. |

API owns `agent_memory.scheduled_jobs` and `scheduled_job_attempts` (under the configured AgentMemory schema). A session advisory lock serializes execution/cancellation across API instances without a long database transaction around model calls. State changes use short transactions. Creation and terminal notification intents share the existing domain outbox, drained by Worker. API reconciles due Scheduled/Retrying/Running rows every 15 seconds, with at most one recovery enqueue per minute per job. MassTransit duplicate deliveries are expected.

After an interrupted run, API rechecks permissions and recovers a saved assistant response if present; otherwise it records NeedsReview without repeating the agent turn. This conservative policy is not a general external-tool idempotency guarantee or resumable framework execution. A result can exist even when post-response memory indexing failed; recovery retains the saved response.

Existing queued agent schedules have no trustworthy originating actor. On delivery they become Blocked legacy records, show an unknown scheduler and never execute or send a completion notification. Their claimed subject is retained for owner inspection. They do not appear before delivery because no application record previously existed. Roll out API and Worker together: an old Worker still contains the removed Owner execution behavior.

Scheduled **notifications** persist the title, body and optional deep link under the returned notification/job ID, with actor and originating conversation. They execute without a model or result conversation. The outcome reports push-provider acceptance counts. Disabled push or missing devices produces Failed; partial or uncertain sends produce NeedsReview and never automatically resend. Recipient tokens are looked up only under the authorized subject.

Notifications scheduled before this extension have no application job record or trusted actor. They keep their original delivery path and cannot be listed or cancelled through Scheduled Jobs, including reminders created after decision 0021 but before this extension. Schedule a new reminder to use the dashboard, bearing in mind the old reminder can still arrive. Immediate notification tools and task-completion alerts remain outside the scheduled-job list.

Transcription jobs, recurring schedules, editing, pause/resume, manual retries and human-approval continuations remain separate work.

## Local verification

Start the synthetic stack using [the demo runbook](synthetic-demo.md), then run:

```powershell
pwsh -NoProfile -File scripts/tests/Test-ScheduledJobs.ps1
pwsh -NoProfile -File scripts/tests/Test-ScheduledNotifications.ps1
```

The script targets only the fixed synthetic endpoint, restarts its Worker, creates sample jobs, temporarily revokes/restores the synthetic scheduling permission, and checks completion, duplicate delivery, read-only result history, isolation, cancellation and denial. It retains sample jobs for dashboard inspection. The synthetic sign-in page is at `http://127.0.0.1:15000/login`.

The notification smoke test also runs in PR CI. It checks immediate dashboard visibility, Worker restart, disabled-push outcome, duplicate delivery, isolation, cancellation and notification-specific permission revocation.
