# 0015: Exercise hints and recording scope in coaching retrieval

- Status: Accepted, retrospective implementation record
- Recorded: 2026-09-11
- Decision date: 2026-09-11
- Evidence: maintainer-reported missed coaching cue; `CoachCheckinService`, `CoachTranscriptProcessingService`, `CoachRetrievalEvaluationTests`

## Context

The maintainer reported missing advice that was present in a transcript. Code inspection found that retrieval required exact generated exercise tags, while the indexer did not emit `yoke`. Passing that tag necessarily excluded the relevant chunk. We have not inspected the original tool invocation or production database, so this is a reproduced failure mechanism, not a confirmed diagnosis of that particular request. The tool also lacked a filename argument for a recording-specific follow-up.

## Decision

Use exercise tags as ranking hints rather than exclusion criteria. Prioritize matching stored tags or English full-text matches in chunk content, then order by vector distance with deterministic ties. This supports existing untagged chunks. Add `yoke` to future indexing. An optional exact, case-insensitive filename restricts the search to that recording within the server-bound subject. Preserve the five-result bound and evidence links.

## Alternatives

Adding only a yoke tag leaves existing chunks broken until reprocessing and repeats the problem for other unknown tags. Retrying only through prompt instructions leaves retrieval dependent on model behavior. A general hybrid ranking system, transcript fallback for unprocessed calls, and index migration are deferred pending a broader evaluation corpus.

## Consequences

Hints can return unrelated candidates when no exercise matches; tool output and chat instructions require supporting evidence before attributing advice. Empty results describe index scope, not absence of advice from source audio. Filename scope is strict and does not interpret wildcard characters. Full-text ranking adds per-query work; no new index, schema migration, or production latency claim is included. Multiword hints require all terms to match for lexical priority, so callers should use an exercise name and keep cue details in the query.

## Delivery and verification

Implemented with fourteen PostgreSQL retrieval/chat baseline cases and a second-utterance processing regression, all synthetic. Six closer distractors force recovery to succeed through lexical priority; another subject has a matching filename and cue to test isolation. Additional cases verify vector ranking without matching tags and fresh chats across service recreation against pre-existing untagged chunks, through the real tool invocation loop with a scripted chat client. See the [evaluation procedure](../runbooks/coach-retrieval-evaluation.md). This is a bounded start on LAB-01/MEMORY-02 in the [roadmap](../plans/roadmap.md), not evidence of live model groundedness or a completed Learning Lab.
