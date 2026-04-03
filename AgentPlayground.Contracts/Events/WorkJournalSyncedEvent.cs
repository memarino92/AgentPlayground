using System;

namespace AgentPlayground.Contracts.Events;

public record WorkJournalSyncedEvent(int EntriesSynced, DateTime SyncedAt);
