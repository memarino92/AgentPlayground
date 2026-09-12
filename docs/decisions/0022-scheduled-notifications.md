# 0022: Notifications in scheduled jobs

- Status: Accepted; implemented
- Recorded: 2026-09-12
- Extends: [0021](0021-scheduled-jobs.md)

## Context and decision

Users expect reminders created by `schedule_notification` to appear in Scheduled Jobs. The first slice persisted only agent tasks; notification scheduling published directly to transport and lost actor attribution.

Persist notifications in the same job store, with a Notification type and stored title, body and optional deep link. Preserve NotificationId as the job ID. Capture signed actor and source conversation, enforce current subject and schedule-notification tool access at creation and execution, and reuse pending cancellation, attempts and reconciliation. Existing records default to AgentTask.

Reuse the existing ID-based Worker dispatch contracts for both types. The API selects the execution path from the stored type: notifications send push without an agent conversation. Record provider acceptance counts rather than claiming receipt by a device. A durable Running marker and the job lock prevent concurrent sends; uncertain interrupted sends require review and are never automatically repeated.

## Alternatives and consequences

A separate reminder dashboard would retain the confusing split. A new transport contract would duplicate the existing ID dispatch machinery without changing its authority. Completing at outbox enqueue would conceal push failures, so notification jobs complete only after the provider send returns. Disabled push, absent devices and partial failure receive explicit outcomes.

Previously queued notifications have no application record or trusted actor. Keep their existing delivery behavior; do not infer an actor or import private transport payloads. They cannot appear retroactively or gain cancellation controls. New scheduled notifications use only the job path. Immediate notification tools and task-completion alerts retain their existing delivery path.

## Evidence and verification

Sources: SchedulingService, AgentEventService, NotificationSchedulerConsumer, PushNotificationConsumer, ScheduledJobExecutionService and decision 0021. Validation: 217 API tests and 45 Web tests passed. New regressions cover creation through the notification tool, actor/source retention, notification-specific permissions, spoofed subjects, cancellation, concurrent duplicate delivery, interrupted sends, disabled push, missing devices and scoped list/detail endpoints. The synthetic smoke test passed 14 checks with a real Worker restart and revoked notification permission; it is included in PR CI. Edge/Playwright verified type/body/attribution, deep-link detail, cancellation, no result conversation, no page errors and no overflow at 390px. Firebase device delivery was not exercised; the synthetic configuration intentionally disables push.
