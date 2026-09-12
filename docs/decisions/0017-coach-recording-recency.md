# 0017: Recording recency and utterance evidence

- Status: Accepted; implemented; bounded live evaluation completed
- Context: Maintainer requests latest coaching guidance and correct exercise attribution.

## Decision

Extend coaching search with latest, recent (default), and relevance modes. Select the latest recording before searching chunks, including unprocessed recordings. Infer recording time conservatively from timestamp filenames; never substitute upload time. Unknown dates prevent a definitive latest-call selection. Exact filename scope takes precedence. Recent mode adds a bounded age penalty within exercise relevance groups; relevance mode preserves historical semantic ranking.

Return filename, date provenance, and individual timestamped utterances within retrieved chunks plus up to four preceding utterances for exercise context. Adjacent exercise mentions do not establish that every cue in a chunk concerns that exercise. Require citations in the initial answer. Normalize invented evidence hostnames only when the exact destination (including subject and timestamp) appears in tool results from the same turn. This repairs formatting, not unsupported claims or arbitrary citations.

## Alternatives and consequences

Upload timestamps misorder batch imports. Prompt-only recency cannot recover excluded evidence. Full semantic exercise segmentation and editable recording dates remain future work; this slice preserves original utterances and exposes ambiguity without inventing categories. No schema migration or re-embedding is required. Filename times have no known timezone and are compared as recording-local times. Date inference is intentionally limited to YYYY-MM-DD HH.mm.ss filenames.

## Validation

Synthetic PostgreSQL regression checks cover date parsing, batch upload order, latest-call scope, missing chunks, absent exercise matches, unknown dates, historical filename scope, subject isolation, and individual utterance citations. The API suite passes locally. Authorized private production-copy replays ran through AgentChatService and the tool registry with gpt-4o-mini and text-embedding-3-small. Live failures exposed omitted optional arguments, missing preceding exercise context, and rewritten citation hostnames; fixes were added and evaluated iteratively. Private source text and operational evidence stay outside Git.

Recent mode retains exercise-match priority and adds up to 0.35 to cosine distance, scaled linearly over 60 days relative to the newest known recording. This is a bounded initial tuning value, not a measured optimum. Unknown dates receive the maximum penalty; latest mode refuses to claim a definitive newest recording when dates are unknown. Tied latest timestamps retain both recordings. No production deployment has been performed.
