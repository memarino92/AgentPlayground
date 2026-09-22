using System;

namespace PersonalAgent.Contracts.Messaging.Events;

public record WorkJournalSyncedEvent(int EntriesSynced, DateTime SyncedAt);
