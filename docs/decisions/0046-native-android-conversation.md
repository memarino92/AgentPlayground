# 0046 Native Android screens and shared presentation code

- Status: Accepted
- Recorded: 2026-11-18
- Evidence: MainPage, ConversationEndpoints, SignedActorFilter and decision 0002.

## Context

The previous Android home screen embedded the website. Browser chat used the signed-in website account, while approval notifications used a separate device registration.

## Decision

Render Android screens with MAUI controls. Use the system browser for account consent. Web grants a time-limited credential bound to a phone-generated proof. Explicit operations use the existing signed actor client and server-side assignment and administrator checks. Conversation, scheduling, automation, coding, recording and settings screens retain their domain APIs.

## Alternatives

A shared Razor class library would reuse HTML components through a BlazorWebView. The native client instead shares settings navigation, recording models and previews, evidence citations, stream decoding and card actions, and renders them with platform controls. Embedding the website keeps its entire navigation but retains the interaction the maintainer wants to replace. Shipping the actor signing key would allow a handset to assert any identity.

## Consequences

Authenticated Android screens need a matching Web deployment. Existing approval registration remains separate. Wider device enrollment and revocation remain tracked in TRUST-06.

## Delivery and verification

On 2026-11-19 the Debug APK built without warnings and launched on a clean API 36.1 emulator. The smoke test exercised signed-out home, menu navigation, the preview recording list and transcript, and connection settings. The Web, Contracts and Mobile test suites passed (90, 25 and 20 tests). Browser consent with a real account, live playback, administrative writes and Firebase delivery still require a matching deployment and device acceptance. See the [mobile guide](../../PersonalAgent.Mobile/README.md).
