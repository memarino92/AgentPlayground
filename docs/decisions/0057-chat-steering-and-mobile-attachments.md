# 0057 Chat steering and mobile attachments

- Status: Proposed
- Recorded: 2026-11-23
- Evidence: recoverable chat runs, signed mobile chat gateway, and sandbox input storage.

## Context

Android disables sending during an active reply and exposes no chat file picker. Web has a file picker and an authorized sandbox input store. Both clients can observe and stop recoverable chat runs.

## Decision

Keep the draft editable and use the existing Stop action on the acknowledged run when the user selects Steer. Wait for its terminal transcript before submitting the captured draft as a new turn. If confirmation fails, preserve the direction in the draft. Expose the Web and Android pickers behind a plus control, and send Android uploads through the same owner-authorized file endpoint as Web. Give the model a bounded preview for Excel workbooks without executing workbook content. Use the stored file reference for full-file processing.

## Alternatives

Replacing an in-flight provider request requires provider-specific support. Adding a second upload service duplicates authorization, storage, and sandbox transfer.

## Consequences

Steering uses the saved partial transcript. Work already performed by a tool remains in effect. A disconnected client can recover an accepted run using the existing protocol. A direction still waiting on Stop stays local until its new turn is submitted. File interpretation outside the workbook preview depends on the configured tools.

## Delivery and verification

Verify steering controls, started-stream handling, scoped attachment access, workbook preview, and Android packaging. Exercise the signed-in Android flow against a matching deployment before release.
