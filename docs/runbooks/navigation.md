# Navigation and transcript playback

Selections and filters belong in the URL. Change them with `NavigationManager.GetUriWithQueryParameter(s)`, preserving unrelated query keys. Push history for deliberate selections; replace it for incremental text filtering. Never navigate again when the URI is unchanged. Read query state on parameter/location changes, including Back/Forward. Keep private message text, drafts, credentials and authorization decisions out of query parameters.

| View | Query parameters |
| --- | --- |
| Chats (`/chat`) | `sessionId`, `profileId`, `drawer=1` |
| Jobs (`/jobs`) | `jobId`, `profileId`, `status`, `before` (ISO timestamp cursor) |
| Coach check-ins (`/coach-checkins/admin`; `/coach-transcripts` is an alias) | `uploadId`, `filter` (0 all, 1 in progress, 2 needs action, 3 completed), `q` |
| Settings (`/admin/settings`) | `section=database`, `sentry`, `telemetry`, or `integrations` |

Legacy `/admin/integrations` opens the integration section of the same Settings page. `/admin/integration-settings` also remains an alias. Backend access checks apply to every requested record regardless of URL or hidden controls.

Chats open with a composer and create their saved ID on the first send. The list remains open when changing chats. Existing chats retain their model on the server; explicit picker changes are saved immediately. New chats prefer the last selected available model in this browser, with catalog fallback. Historical blank records are left untouched.

The recording page and evidence drawer share transcript rows, timestamp seeking and playback following. Follow transcript is on by default and can be unchecked to read elsewhere while listening. Filenames beginning with `yyyy-MM-dd HH.mm.ss.` display a friendly conversation date and the original filename below; unparsed names remain filenames. Owner upload, speaker overrides and audio management remain absent for coaches.

## Browser verification

Use the synthetic demo, with no private recordings or provider calls:

1. Select a job and status, follow its conversation link, then press Back once. Confirm the job and filter are restored.
2. Open Chats, select two records, and press Back. Confirm the previous chat/model returns and the list remains open.
3. Open a fresh chat and click New chat repeatedly. No saved row should appear until Send. Change the model and verify it survives reopening the chat; check a new chat uses the last available choice.
4. Open the menu and click outside it; verify dismissal. Choose a Settings section and verify its query URL.
5. Select a recording, seek to an utterance and confirm the player time and active row agree. Back restores the earlier recording. Check both inline and drawer playback, and disable following while listening.
6. Sign in as Assigned coach and open the same check-in route. Transcript/playback remain available while owner controls are absent.

Automated tests supplement browser checks for unavailable model IDs, other-owner subjects, read-only scheduled records, and missing/invalid filename dates. Synthetic silence verifies playback/transport, not speech alignment.

Verified 2026-09-13: 53 Web tests and 218 API tests passed. The API suite requires Docker and Windows event-log access in this Windows environment. The synthetic browser verified single-Back job/filter restoration through the result conversation, chat/list retention, recording Back, outside-menu dismissal, timestamp/active-row agreement, Settings integration content, and coach playback with no owner controls. No production deployment or speech-alignment check was performed.
