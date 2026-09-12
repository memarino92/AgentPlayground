using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using PersonalAgent.Configuration;

namespace PersonalAgent.Services;

internal class AgentMemorySchemaInitializer(IOptions<AgentMemoryOptions> options, ILogger<AgentMemorySchemaInitializer> logger) : IHostedService
{
    private readonly AgentMemoryOptions _options = options.Value;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.CreateInfrastructure) return;

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await ExecuteNonQueryAsync(connection, "CREATE EXTENSION IF NOT EXISTS vector;", cancellationToken);

        await ExecuteNonQueryAsync(connection, BuildSchemaSql(), cancellationToken);
        logger.LogInformation("Ensured agent memory schema {Schema} exists", _options.Schema);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ExecuteNonQueryAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string BuildSchemaSql()
    {
        var schema = QuoteIdentifier(_options.Schema);
        var sessionsTable = QualifiedTableName(_options.SessionsTableName);
        var transcriptMessagesTable = QualifiedTableName(_options.TranscriptMessagesTableName);
        var memoryRecordsTable = QualifiedTableName(_options.MemoryRecordsTableName);
        var mobileDeviceTokensTable = QualifiedTableName(_options.MobileDeviceTokensTableName);
        var agentApprovalsTable = QualifiedTableName(_options.AgentApprovalsTableName);
        var coachCallUploadsTable = QualifiedTableName("coach_call_uploads");
        var coachCallUtterancesTable = QualifiedTableName("coach_call_utterances");
        var coachCallSessionsTable = QualifiedTableName("coach_call_sessions");
        var coachCallChunksTable = QualifiedTableName("coach_call_chunks");
        var coachCallSpeakerOverridesTable = QualifiedTableName("coach_call_speaker_overrides");
        var toolRolePermissionsTable = QualifiedTableName("tool_role_permissions");
        var coachProfileAssignmentsTable = QualifiedTableName("coach_profile_assignments");
        var vectorColumnDefinition = _options.EnableSemanticMemory
            ? $", embedding vector({_options.VectorDimensions}) NULL"
            : string.Empty;
        var vectorIndexSql = _options.EnableSemanticMemory
            ? $"CREATE INDEX IF NOT EXISTS {QuoteIdentifier($"ix_{_options.MemoryRecordsTableName}_embedding")} ON {memoryRecordsTable} USING hnsw (embedding vector_cosine_ops);"
            : string.Empty;
        var alterMemoryTableSql = _options.EnableSemanticMemory
            ? $"ALTER TABLE {memoryRecordsTable} ADD COLUMN IF NOT EXISTS embedding vector({_options.VectorDimensions}) NULL;"
            : string.Empty;

        return $"""
            CREATE SCHEMA IF NOT EXISTS {schema};

            {AgentPlayground.Contracts.Messaging.CoachCallOutbox.SchemaSql(_options.Schema)}
            {ScheduledJobStore.SchemaSql(_options.Schema)}

            CREATE TABLE IF NOT EXISTS {sessionsTable}
            (
                session_id uuid PRIMARY KEY,
                profile_id text NOT NULL,
                session_state jsonb NOT NULL,
                session_state_version integer NOT NULL,
                last_message_seq bigint NOT NULL DEFAULT 0,
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL,
                last_message_at timestamptz NULL
            );

            ALTER TABLE {sessionsTable} ADD COLUMN IF NOT EXISTS profile_id text;
            UPDATE {sessionsTable} SET profile_id = 'anonymous' WHERE profile_id IS NULL;
            ALTER TABLE {sessionsTable} ALTER COLUMN profile_id SET NOT NULL;
            CREATE INDEX IF NOT EXISTS {QuoteIdentifier($"ix_{_options.SessionsTableName}_updated_at")} ON {sessionsTable} (updated_at DESC);
            CREATE INDEX IF NOT EXISTS {QuoteIdentifier($"ix_{_options.SessionsTableName}_profile_id_updated_at")} ON {sessionsTable} (profile_id, updated_at DESC);

            ALTER TABLE {sessionsTable} ADD COLUMN IF NOT EXISTS actor_id text;
            ALTER TABLE {sessionsTable} ADD COLUMN IF NOT EXISTS role_name text;
            ALTER TABLE {sessionsTable} ADD COLUMN IF NOT EXISTS memory_profile_id text;
            UPDATE {sessionsTable}
            SET actor_id = profile_id,
                role_name = 'Owner',
                memory_profile_id = profile_id
            WHERE actor_id IS NULL OR role_name IS NULL OR memory_profile_id IS NULL;
            ALTER TABLE {sessionsTable} ALTER COLUMN actor_id SET NOT NULL;
            ALTER TABLE {sessionsTable} ALTER COLUMN role_name SET NOT NULL;
            ALTER TABLE {sessionsTable} ALTER COLUMN memory_profile_id SET NOT NULL;
            CREATE INDEX IF NOT EXISTS {QuoteIdentifier($"ix_{_options.SessionsTableName}_actor_updated_at")} ON {sessionsTable} (actor_id, updated_at DESC);

            CREATE TABLE IF NOT EXISTS {toolRolePermissionsTable}
            (
                role_name text NOT NULL,
                tool_key text NOT NULL,
                is_enabled boolean NOT NULL,
                updated_at timestamptz NOT NULL,
                updated_by text NOT NULL,
                PRIMARY KEY (role_name, tool_key)
            );

            CREATE TABLE IF NOT EXISTS {coachProfileAssignmentsTable}
            (
                normalized_coach_email text NOT NULL,
                coach_email text NOT NULL,
                coach_actor_id text NULL,
                subject_profile_id text NOT NULL,
                is_active boolean NOT NULL,
                updated_at timestamptz NOT NULL,
                updated_by text NOT NULL,
                PRIMARY KEY (normalized_coach_email, subject_profile_id)
            );
            CREATE INDEX IF NOT EXISTS {QuoteIdentifier("ix_coach_profile_assignments_actor")} ON {coachProfileAssignmentsTable} (coach_actor_id, is_active);

            CREATE TABLE IF NOT EXISTS {transcriptMessagesTable}
            (
                message_id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                session_id uuid NOT NULL REFERENCES {sessionsTable} (session_id) ON DELETE CASCADE,
                message_seq bigint NOT NULL,
                role text NOT NULL,
                content text NOT NULL,
                metadata jsonb NULL,
                created_at timestamptz NOT NULL,
                CONSTRAINT {QuoteIdentifier($"uq_{_options.TranscriptMessagesTableName}_session_seq")} UNIQUE (session_id, message_seq)
            );

            CREATE INDEX IF NOT EXISTS {QuoteIdentifier($"ix_{_options.TranscriptMessagesTableName}_session_created_at")} ON {transcriptMessagesTable} (session_id, created_at);

            CREATE TABLE IF NOT EXISTS {memoryRecordsTable}
            (
                memory_id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                session_id uuid NULL REFERENCES {sessionsTable} (session_id) ON DELETE CASCADE,
                profile_id text NULL,
                source_message_id bigint NULL REFERENCES {transcriptMessagesTable} (message_id) ON DELETE SET NULL,
                memory_kind text NOT NULL,
                content text NOT NULL,
                metadata jsonb NULL,
                embedding_model text NULL{vectorColumnDefinition},
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL
            );

            {alterMemoryTableSql}
            ALTER TABLE {memoryRecordsTable} ADD COLUMN IF NOT EXISTS profile_id text;
            UPDATE {memoryRecordsTable} mr
            SET profile_id = s.profile_id
            FROM {sessionsTable} s
            WHERE mr.profile_id IS NULL
              AND mr.session_id = s.session_id;
            CREATE INDEX IF NOT EXISTS {QuoteIdentifier($"ix_{_options.MemoryRecordsTableName}_session_created_at")} ON {memoryRecordsTable} (session_id, created_at DESC);
            CREATE INDEX IF NOT EXISTS {QuoteIdentifier($"ix_{_options.MemoryRecordsTableName}_profile_created_at")} ON {memoryRecordsTable} (profile_id, created_at DESC);
            {vectorIndexSql}

            CREATE TABLE IF NOT EXISTS {mobileDeviceTokensTable}
            (
                profile_id text NOT NULL,
                device_id text NOT NULL,
                platform text NOT NULL,
                push_token text NOT NULL,
                app_version text NULL,
                registered_at timestamptz NOT NULL,
                last_seen_at timestamptz NOT NULL,
                CONSTRAINT {QuoteIdentifier($"pk_{_options.MobileDeviceTokensTableName}")} PRIMARY KEY (profile_id, device_id)
            );

            CREATE INDEX IF NOT EXISTS {QuoteIdentifier($"ix_{_options.MobileDeviceTokensTableName}_profile_last_seen")} ON {mobileDeviceTokensTable} (profile_id, last_seen_at DESC);
            CREATE INDEX IF NOT EXISTS {QuoteIdentifier($"ix_{_options.MobileDeviceTokensTableName}_push_token")} ON {mobileDeviceTokensTable} (push_token);

            CREATE TABLE IF NOT EXISTS {agentApprovalsTable}
            (
                approval_id uuid PRIMARY KEY,
                profile_id text NOT NULL,
                session_id text NOT NULL,
                tool_name text NOT NULL,
                action_summary text NOT NULL,
                requested_by text NOT NULL,
                requested_at timestamptz NOT NULL,
                expires_at timestamptz NOT NULL,
                status text NOT NULL,
                decision_at timestamptz NULL,
                decided_by text NULL,
                reason text NULL
            );

            CREATE INDEX IF NOT EXISTS {QuoteIdentifier($"ix_{_options.AgentApprovalsTableName}_profile_requested")} ON {agentApprovalsTable} (profile_id, requested_at DESC);
            CREATE INDEX IF NOT EXISTS {QuoteIdentifier($"ix_{_options.AgentApprovalsTableName}_session_requested")} ON {agentApprovalsTable} (session_id, requested_at DESC);

            CREATE TABLE IF NOT EXISTS {coachCallUploadsTable}
            (
                upload_id uuid PRIMARY KEY,
                profile_id text NOT NULL,
                session_id uuid NULL,
                correlation_id uuid NOT NULL,
                original_file_name text NOT NULL,
                mime_type text NOT NULL,
                size_bytes bigint NOT NULL,
                file_hash text NOT NULL,
                audio_bytes bytea NULL,
                status text NOT NULL,
                error text NULL,
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS {QuoteIdentifier("ux_coach_call_uploads_profile_file_hash")} ON {coachCallUploadsTable} (profile_id, file_hash);
            CREATE INDEX IF NOT EXISTS {QuoteIdentifier("ix_coach_call_uploads_profile_created")} ON {coachCallUploadsTable} (profile_id, created_at DESC);
            CREATE INDEX IF NOT EXISTS {QuoteIdentifier("ix_coach_call_uploads_status_updated")} ON {coachCallUploadsTable} (status, updated_at DESC);

            CREATE TABLE IF NOT EXISTS {coachCallSessionsTable}
            (
                session_id uuid PRIMARY KEY,
                upload_id uuid NOT NULL REFERENCES {coachCallUploadsTable} (upload_id) ON DELETE CASCADE,
                profile_id text NOT NULL,
                transcript_text text NOT NULL DEFAULT '',
                summary_markdown text NOT NULL DEFAULT '',
                summary_json jsonb NOT NULL DEFAULT jsonb_build_object(),
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL
            );

            CREATE INDEX IF NOT EXISTS {QuoteIdentifier("ix_coach_call_sessions_profile_updated")} ON {coachCallSessionsTable} (profile_id, updated_at DESC);

            CREATE TABLE IF NOT EXISTS {QualifiedTableName("transcription_jobs")}
            (
                upload_id uuid PRIMARY KEY REFERENCES {coachCallUploadsTable}(upload_id) ON DELETE CASCADE,
                provider_job_id text NULL,
                deadline timestamptz NOT NULL,
                result jsonb NULL
            );

            CREATE TABLE IF NOT EXISTS {coachCallUtterancesTable}
            (
                utterance_id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                session_id uuid NOT NULL REFERENCES {coachCallSessionsTable} (session_id) ON DELETE CASCADE,
                speaker_label integer NOT NULL,
                speaker_role text NOT NULL,
                start_ms integer NOT NULL,
                end_ms integer NOT NULL,
                confidence double precision NOT NULL,
                content text NOT NULL
            );

            CREATE INDEX IF NOT EXISTS {QuoteIdentifier("ix_coach_call_utterances_session_start")} ON {coachCallUtterancesTable} (session_id, start_ms);

            CREATE TABLE IF NOT EXISTS {coachCallSpeakerOverridesTable}
            (
                upload_id uuid NOT NULL REFERENCES {coachCallUploadsTable} (upload_id) ON DELETE CASCADE,
                speaker_label integer NOT NULL,
                speaker_role text NOT NULL,
                created_at timestamptz NOT NULL,
                PRIMARY KEY (upload_id, speaker_label)
            );

            CREATE TABLE IF NOT EXISTS {coachCallChunksTable}
            (
                chunk_id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                session_id uuid NOT NULL REFERENCES {coachCallSessionsTable} (session_id) ON DELETE CASCADE,
                chunk_index integer NOT NULL,
                start_ms integer NOT NULL,
                end_ms integer NOT NULL,
                content text NOT NULL,
                speaker_mix text NOT NULL,
                exercise_tags jsonb NOT NULL,
                intent_tags jsonb NOT NULL,
                priority_tags jsonb NOT NULL,
                metadata jsonb NOT NULL,
                embedding vector({_options.VectorDimensions}) NULL,
                created_at timestamptz NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS {QuoteIdentifier("ux_coach_call_chunks_session_chunk_index")} ON {coachCallChunksTable} (session_id, chunk_index);
            CREATE INDEX IF NOT EXISTS {QuoteIdentifier("ix_coach_call_chunks_session_created")} ON {coachCallChunksTable} (session_id, created_at DESC);
            CREATE INDEX IF NOT EXISTS {QuoteIdentifier("ix_coach_call_chunks_exercise_tags")} ON {coachCallChunksTable} USING gin (exercise_tags);
            CREATE INDEX IF NOT EXISTS {QuoteIdentifier("ix_coach_call_chunks_intent_tags")} ON {coachCallChunksTable} USING gin (intent_tags);
            CREATE INDEX IF NOT EXISTS {QuoteIdentifier("ix_coach_call_chunks_priority_tags")} ON {coachCallChunksTable} USING gin (priority_tags);
            CREATE INDEX IF NOT EXISTS {QuoteIdentifier("ix_coach_call_chunks_metadata")} ON {coachCallChunksTable} USING gin (metadata);
            CREATE INDEX IF NOT EXISTS {QuoteIdentifier("ix_coach_call_chunks_embedding")} ON {coachCallChunksTable} USING hnsw (embedding vector_cosine_ops);
            """;
    }

    private string QualifiedTableName(string tableName) => $"{QuoteIdentifier(_options.Schema)}.{QuoteIdentifier(tableName)}";

    private static string QuoteIdentifier(string identifier) => new NpgsqlCommandBuilder().QuoteIdentifier(identifier);
}
