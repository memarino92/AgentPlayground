# Navigation and transcript playback

Selections and filters belong in the URL. Change them with `NavigationManager.GetUriWithQueryParameter(s)`, preserving unrelated query keys. Push history for deliberate selections; replace it for incremental text filtering. Never navigate again when the URI is unchanged. Read query state on parameter/location changes, including Back/Forward. Keep private message text, drafts, credentials and authorization decisions out of query parameters.

| View | Query parameters |
| --- | --- |
| Chat (`/chat`) | `profileId`, `drawer=1`; saved-history links also use `sessionId` and optional `message` sequence |
| Jobs (`/jobs`) | `jobId`, `profileId`, `status`, `before` (ISO timestamp cursor) |
| Coach check-ins (`/coach-checkins/admin`; `/coach-transcripts` is an alias) | `uploadId`, `filter` (0 all, 1 in progress, 2 needs action, 3 completed), `q` |
| Settings (`/admin/settings`) | `section=database`, `sentry`, `telemetry`, or `integrations` |

Legacy `/admin/integrations` opens the integration section of the same Settings page. `/admin/integration-settings` also remains an alias. Backend access checks apply to every requested record regardless of URL or hidden controls.

Chat resumes one server-owned conversation for the signed-in actor and selected profile. The default URL has no session ID. History and job deep links open read-only saved conversations; source links add `message=<sequence>` to highlight an exchange, including exchanges within the current conversation. Back to conversation returns to the composer. Model selection persists on the server. Start fresh and `/clear` save a context boundary without deleting history or creating a user-managed session. See [the current chat runbook](continuous-conversation.md).

The recording page and evidence drawer share transcript rows, timestamp seeking and playback following. Follow transcript is on by default and can be unchecked to read elsewhere while listening. Filenames beginning with `yyyy-MM-dd HH.mm.ss.` display a friendly conversation date and the original filename below; unparsed names remain filenames. Owner upload, speaker overrides and audio management remain absent for coaches.

## Browser verification

Use the synthetic demo, with no private recordings or provider calls:

1. Select a job and status, follow its conversation link, then press Back once. Confirm the job and filter are restored.
2. Open Chat, search History, and follow a result. Confirm the referenced message is highlighted and saved history is read-only. Back restores the prior navigation state; Back to conversation restores the composer.
3. Change the model and verify it survives reopening Chat. Send `/clear`, reload, and confirm the composer starts fresh while the previous exchange remains searchable in History.
4. Open the menu and click outside it; verify dismissal. Choose a Settings section and verify its query URL.
5. Select a recording, seek to an utterance and confirm the player time and active row agree. Back restores the earlier recording. Check both inline and drawer playback, and disable following while listening.
6. Sign in as Assigned coach and open the same check-in route. Transcript/playback remain available while owner controls are absent.

Automated tests supplement browser checks for unavailable model IDs, other-owner subjects, read-only scheduled records, and missing/invalid filename dates. Synthetic silence verifies playback/transport, not speech alignment.

Verified 2026-09-13: 53 Web tests and 218 API tests passed. The API suite requires Docker and Windows event-log access in this Windows environment. The synthetic browser verified single-Back job/filter restoration through the result conversation, chat/list retention, recording Back, outside-menu dismissal, timestamp/active-row agreement, Settings integration content, and coach playback with no owner controls. No production deployment or speech-alignment check was performed.

Verified 2026-09-21: continuous conversation, persistent interactive cards, history search with the selected profile, and same-conversation source highlighting passed synthetic browser checks. The older chat creation checks above describe the September 13 implementation only. Current test counts and scope are recorded in [the chat runbook](continuous-conversation.md).

## Color theme

Use **Theme** in the site header to choose **System**, **Light**, or **Dark**. System follows the operating system and updates while the page is open. The choice is saved per browser and synchronized across tabs; it is available before sign-in. If browser storage is blocked, switching still works for the current page.

Custom styles and MudBlazor controls use the same resolved theme. During prerendering, custom styles follow the operating system; the saved override and MudBlazor palette apply when the interactive connection starts. Theme-aware CSS uses `light-dark()` and requires a modern browser.

Verify preference handling with `node --test scripts/tests/theme.test.mjs` and the layout integration with `dotnet test PersonalAgent.Web.Tests/PersonalAgent.Web.Tests.csproj`.
