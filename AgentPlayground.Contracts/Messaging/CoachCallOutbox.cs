using System.Text.Json;
using AgentPlayground.Contracts.Commands;
using AgentPlayground.Contracts.Events;
using MassTransit;
using Npgsql;

namespace AgentPlayground.Contracts.Messaging;

public static class CoachCallOutbox
{
    public static string Table(string Schema) => $"{new NpgsqlCommandBuilder().QuoteIdentifier(Schema)}.coach_call_outbox";

    public static string SchemaSql(string Schema) => $"""
        CREATE TABLE IF NOT EXISTS {Table(Schema)} (
            message_id uuid PRIMARY KEY,
            message_type text NOT NULL,
            payload jsonb NOT NULL,
            created_at timestamptz NOT NULL DEFAULT now()
        );
        CREATE INDEX IF NOT EXISTS ix_coach_call_outbox_pending ON {Table(Schema)} (created_at, message_id);
        """;

    public static async Task EnqueueAsync<T>(NpgsqlTransaction Transaction, string Schema, T Message, CancellationToken CancellationToken) where T : class
    {
        if (Message is not (ProcessCoachTranscriptCommand or CoachCallStatusChangedEvent or CoachCallTranscriptionCompletedEvent or CoachCallProcessingCompletedEvent or CoachCallProcessingFailedEvent))
            throw new ArgumentException("Unsupported coach call outbox message.", nameof(Message));
        await using var Command = new NpgsqlCommand($"INSERT INTO {Table(Schema)} (message_id, message_type, payload) VALUES (@id, @type, @payload::jsonb)", Transaction.Connection, Transaction);
        Command.Parameters.AddWithValue("id", Guid.NewGuid());
        Command.Parameters.AddWithValue("type", typeof(T).Name);
        Command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(Message));
        await Command.ExecuteNonQueryAsync(CancellationToken);
    }

    // Keep the row locked until transport acceptance. A lost acknowledgement intentionally replays the same ID.
    public static async Task<bool> DispatchOneAsync(string ConnectionString, string Schema, Func<Guid, object, CancellationToken, Task> Deliver, CancellationToken CancellationToken)
    {
        await using var Connection = new NpgsqlConnection(ConnectionString);
        await Connection.OpenAsync(CancellationToken);
        await using var Transaction = await Connection.BeginTransactionAsync(CancellationToken);
        Guid Id;
        object Message;
        await using (var Query = new NpgsqlCommand($"SELECT message_id, message_type, payload::text FROM {Table(Schema)} ORDER BY created_at, message_id LIMIT 1 FOR UPDATE SKIP LOCKED", Connection, Transaction))
        await using (var Reader = await Query.ExecuteReaderAsync(CancellationToken))
        {
            if (!await Reader.ReadAsync(CancellationToken)) return false;
            Id = Reader.GetGuid(0);
            var Payload = Reader.GetString(2);
            Message = Reader.GetString(1) switch
            {
                nameof(ProcessCoachTranscriptCommand) => JsonSerializer.Deserialize<ProcessCoachTranscriptCommand>(Payload)!,
                nameof(CoachCallStatusChangedEvent) => JsonSerializer.Deserialize<CoachCallStatusChangedEvent>(Payload)!,
                nameof(CoachCallTranscriptionCompletedEvent) => JsonSerializer.Deserialize<CoachCallTranscriptionCompletedEvent>(Payload)!,
                nameof(CoachCallProcessingCompletedEvent) => JsonSerializer.Deserialize<CoachCallProcessingCompletedEvent>(Payload)!,
                nameof(CoachCallProcessingFailedEvent) => JsonSerializer.Deserialize<CoachCallProcessingFailedEvent>(Payload)!,
                _ => throw new InvalidOperationException("Unknown coach call outbox message type.")
            };
        }
        await Deliver(Id, Message, CancellationToken);
        await using var Delete = new NpgsqlCommand($"DELETE FROM {Table(Schema)} WHERE message_id = @id", Connection, Transaction);
        Delete.Parameters.AddWithValue("id", Id);
        await Delete.ExecuteNonQueryAsync(CancellationToken);
        await Transaction.CommitAsync(CancellationToken);
        return true;
    }

    public static async Task DeliverAsync(IBus Bus, Guid Id, object Message, CancellationToken CancellationToken)
    {
        if (Message is ProcessCoachTranscriptCommand Command)
        {
            var Endpoint = await Bus.GetSendEndpoint(new Uri($"queue:{MessagingEndpointNames.CoachCallProcessing}"));
            await Endpoint.Send(Command, Context => Context.MessageId = Id, CancellationToken);
            return;
        }
        await Bus.Publish(Message, Message.GetType(), Context => Context.MessageId = Id, CancellationToken);
    }
}
