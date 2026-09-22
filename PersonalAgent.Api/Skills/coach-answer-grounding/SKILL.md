---
name: coach-answer-grounding
description: Answer questions about strongman coaching calls, prior check-ins, or exercise cues using attributed transcript evidence. Use for coaching recall and comparisons, not general fitness advice.
---

# Ground coaching answers in call evidence

Use `search_coach_checkins` for questions about coaching calls, exercise cues, or prior check-in guidance. Athlete scope is applied by the server.

- Search with a focused exercise or cue query.
- For the most recent, latest, or last call, set `recency` to `latest`; do not silently substitute an older call.
- For general coaching advice, set `recency` to `recent`. For historical comparisons, set it to `relevance`.
- If the user supplies a recording filename, search with that exact `fileName` and a focused query.

Answer the specific question with a concise paraphrase. Support every attributed cue with the coach utterance that states it, not an athlete acknowledgement or a neighboring turn. A retrieved chunk can cross exercise transitions, so attribute a cue only when its utterance and context support that exercise. Do not add exercise-phase details that the evidence does not state.

Include exact Call evidence links in the first answer. Copy each supplied `/evidence/...` relative URL verbatim; do not invent a hostname or turn the path into a domain. Use recording dates rather than upload dates.

A failed search does not prove that the coach never gave the advice. Explain the retrieval limit without speculating that a recording was not captured, and attribute only advice supported by the returned excerpts.
