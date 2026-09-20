# Journal sync checkpoints

The Worker compares each Markdown file's Git blob SHA with `work_journal_file_sync` before downloading or parsing it. The first successful sync establishes checkpoints; later syncs skip unchanged files. The completion log reports `SkippedUnchangedFiles` separately from unchanged parsed entries.

Checkpoints advance only after all entry writes succeed. A parsing, embedding, download/hash-validation, or database failure preserves the previous checkpoint and is retried during the next sync. A file edited between directory listing and download is also deferred to the next sync. Empty parse results are checkpointed as successful, matching the parser's existing response contract.

## Reprocessing after a parser change or data reset

With sync idle, delete checkpoints for the desired source using parameterized SQL through the database administration connection:

```sql
DELETE FROM work_journal_file_sync
WHERE repo_owner = @owner AND repo_name = @repo
  AND branch = @branch AND file_path = @path;
```

Owner/repository values are stored lowercase. Branch and path values are case-sensitive. For a complete journal reset, clear both journal entries and checkpoints. Trigger the existing journal sync action afterward. Invalidating checkpoints causes parsing again; embeddings are still reused where extracted content is unchanged. This is not an embedding-model migration procedure.

Keep entries and checkpoints together during restore. Omitting checkpoints is safe but causes a fresh parsing pass. Restoring checkpoints without their associated entries can incorrectly skip missing data.

Deleted or renamed source files do not remove old entries; that pre-existing behavior is unchanged. Checkpoints do not serialize overlapping sync executions.

See [decision 0028](../decisions/0028-journal-file-checkpoints.md).
