# 0025: Stereo coach attribution

- Status: Accepted; implemented
- Recorded: 2026-09-14
- Decision date: 2026-09-14
- Evidence: maintainer-confirmed phone recording convention; transcription adapter, neutral contract, Worker attribution, transcript presentation
- Extends: [0004: Agent service boundary](0004-agent-service-boundary.md)

## Context

Coach calls are consistently recorded as stereo on the same phone: the coach is on the left channel and the athlete is on the right channel. Provider diarization does not know that recording convention, has repeatedly assigned unreliable anonymous speakers, and forces manual review before processing. The application must retain the semantic `coach` and `athlete` roles because retrieval and downstream processing depend on them, while human-readable transcripts should use the participants' names.

## Decision

Submit new recordings to AssemblyAI as multichannel audio with diarization disabled. Carry the provider's one-based audio channel through the provider-neutral response, then map channel 1 (left) to `coach` and channel 2 (right) to `athlete` in Worker. Automatically continue processing only when every utterance has a recognized channel. Cached results created before channel metadata was added, missing channel metadata, and unexpected channels remain `unknown` and pause for the existing manual role-review workflow.

Keep `speaker_role` as the persisted domain attribution. Add database-first `CoachCheckins:CoachName` and `CoachCheckins:AthleteName` API settings, initially `Andrew` and `Michael`. API transcript responses and text downloads present each name together with its role; chunks, retrieval evidence, speaker overrides, and processing continue to use the role. Name changes affect presentation after API restart and do not rewrite stored utterances or chunks.

## Alternatives

Continuing provider diarization was rejected because it ignores reliable source-channel evidence and is the reported source of recurring manual corrections. Inferring identities from transcript language or voice recognition was rejected as less deterministic and more privacy-sensitive. Replacing persisted roles with names was rejected because names are presentation configuration and roles drive authorization-independent domain behavior.

## Consequences

The normal two-channel phone recording proceeds without manual speaker review and preserves deterministic attribution through overlaps. Recordings that do not follow the left/right convention will be confidently misattributed, so the convention is an operational precondition. Mono recordings and provider results without channel metadata do not receive guessed roles. Existing completed transcripts are not reprocessed; configured names are applied when they are read.

The neutral contract adds optional channel metadata so cached older JSON remains deserializable. The startup initializer adds missing non-secret name rows without overwriting existing Shared or Api values. The existing database settings editor can change them; API restart is explicit because these options are startup-bound.

## Delivery and verification

Provider and Worker unit tests cover multichannel submission, channel parsing, role mapping, and legacy results without channel metadata. Web tests cover the shared transcript surface, and PostgreSQL recovery tests cover automatic continuation plus persisted roles. A live-provider recording check remains necessary because automated tests do not submit private audio to AssemblyAI. See the [transcription gateway runbook](../runbooks/transcription-gateway.md).
