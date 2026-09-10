using System.Globalization;
using Microsoft.Extensions.Options;
using Npgsql;
using PersonalAgent.Configuration;

namespace PersonalAgent.Development;

internal sealed class SyntheticDataInitializer(IOptions<AgentMemoryOptions> Options) : IHostedService
{
    public async Task StartAsync(CancellationToken CancellationToken)
    {
        await using var Connection = new NpgsqlConnection(Options.Value.ConnectionString);
        await Connection.OpenAsync(CancellationToken);
        await using var Transaction = await Connection.BeginTransactionAsync(CancellationToken);
        await using var Command = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(730910);
            INSERT INTO agent_memory.coach_profile_assignments
                (normalized_coach_email, coach_email, coach_actor_id, subject_profile_id, is_active, updated_at, updated_by)
            VALUES ('COACH@EXAMPLE.TEST', 'coach@example.test', 'google:synthetic-coach', 'demo-owner', true, now(), 'synthetic-seed')
            ON CONFLICT DO NOTHING;
            INSERT INTO agent_memory.sessions
                (session_id, profile_id, actor_id, role_name, memory_profile_id, session_state, session_state_version, last_message_seq, created_at, updated_at, last_message_at)
            VALUES
                ('10000000-0000-0000-0000-000000000001', 'demo-owner', 'demo-owner', 'Owner', 'demo-owner', '{"ModelId":"synthetic-demo"}', 1, 2, now(), now(), now()),
                ('10000000-0000-0000-0000-000000000002', 'demo-other', 'demo-other', 'Owner', 'demo-other', '{"ModelId":"synthetic-demo"}', 1, 2, now(), now(), now())
            ON CONFLICT DO NOTHING;
            INSERT INTO agent_memory.transcript_messages (session_id, message_seq, role, content, created_at)
            VALUES
                ('10000000-0000-0000-0000-000000000001', 1, 'user', 'Remember that my favorite exercise is deadlift.', now()),
                ('10000000-0000-0000-0000-000000000001', 2, 'assistant', 'Synthetic example saved for your next training check-in.', now()),
                ('10000000-0000-0000-0000-000000000002', 1, 'user', 'Remember that my favorite exercise is overhead press.', now()),
                ('10000000-0000-0000-0000-000000000002', 2, 'assistant', 'This synthetic conversation belongs to the other owner.', now())
            ON CONFLICT DO NOTHING;
            INSERT INTO agent_memory.memory_records
                (session_id, profile_id, memory_kind, content, embedding_model, embedding, created_at, updated_at)
            SELECT '10000000-0000-0000-0000-000000000001', 'demo-owner', 'user', @content, 'synthetic-token-hash-v1', @vector::vector, now(), now()
            WHERE NOT EXISTS (SELECT 1 FROM agent_memory.memory_records WHERE profile_id = 'demo-owner' AND content = @content);
            INSERT INTO agent_memory.coach_call_uploads
                (upload_id, profile_id, session_id, correlation_id, original_file_name, mime_type, size_bytes, file_hash, audio_bytes, status, created_at, updated_at)
            VALUES ('20000000-0000-0000-0000-000000000001', 'demo-owner', '30000000-0000-0000-0000-000000000001',
                '40000000-0000-0000-0000-000000000001', 'synthetic-speaker-review.wav', 'audio/wav', 0, 'synthetic-seed-v1', NULL, 'AwaitingSpeakerOverride', now(), now())
            ON CONFLICT DO NOTHING;
            INSERT INTO agent_memory.coach_call_sessions (session_id, upload_id, profile_id, transcript_text, created_at, updated_at)
            VALUES ('30000000-0000-0000-0000-000000000001', '20000000-0000-0000-0000-000000000001', 'demo-owner',
                'How can I improve my deadlift setup? Brace before the pull and keep the bar close to your shins.', now(), now())
            ON CONFLICT DO NOTHING;
            INSERT INTO agent_memory.coach_call_utterances (session_id, speaker_label, speaker_role, start_ms, end_ms, confidence, content)
            SELECT '30000000-0000-0000-0000-000000000001', label, 'Unknown', start_ms, end_ms, 1, content
            FROM (VALUES (0, 0, 4000, 'How can I improve my deadlift setup?'),
                         (1, 4000, 9000, 'Brace before the pull and keep the bar close to your shins.')) AS seed(label, start_ms, end_ms, content)
            WHERE NOT EXISTS (SELECT 1 FROM agent_memory.coach_call_utterances WHERE session_id = '30000000-0000-0000-0000-000000000001');
            """, Connection, Transaction);
        const string Content = "Remember that my favorite exercise is deadlift.";
        Command.Parameters.AddWithValue("content", Content);
        Command.Parameters.AddWithValue("vector", "[" + string.Join(',', SyntheticEmbeddingService.Embed(Content).Select(Value => Value.ToString("R", CultureInfo.InvariantCulture))) + "]");
        await Command.ExecuteNonQueryAsync(CancellationToken);
        await Transaction.CommitAsync(CancellationToken);
    }

    public Task StopAsync(CancellationToken CancellationToken) => Task.CompletedTask;
}
