using Microsoft.Extensions.Options;
using Npgsql;
using PersonalAgent.Configuration;
using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal sealed class PostgresCoachAssignmentStore(IOptions<AgentMemoryOptions> options) : ICoachAssignmentStore
{
    private readonly AgentMemoryOptions _options = options.Value;

    public async Task<IReadOnlyList<string>> GetAssignedProfilesAsync(string coachActorId, string coachEmail, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE {TableName}
            SET coach_actor_id = @coachActorId, updated_at = @updatedAt
            WHERE normalized_coach_email = @coachEmail
              AND is_active
              AND (coach_actor_id IS NULL OR coach_actor_id = @coachActorId);

            SELECT subject_profile_id
            FROM {TableName}
            WHERE coach_actor_id = @coachActorId
              AND normalized_coach_email = @coachEmail
              AND is_active
            ORDER BY subject_profile_id;
            """;
        command.Parameters.AddWithValue("coachActorId", coachActorId);
        command.Parameters.AddWithValue("coachEmail", NormalizeEmail(coachEmail));
        command.Parameters.AddWithValue("updatedAt", DateTimeOffset.UtcNow);

        var profiles = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) profiles.Add(reader.GetString(0));
        return profiles;
    }

    public async Task<IReadOnlyList<CoachProfileAssignment>> GetAssignmentsAsync(string subjectProfileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT coach_email, coach_actor_id, subject_profile_id, is_active, updated_at
            FROM {TableName}
            WHERE subject_profile_id = @subjectProfileId
            ORDER BY normalized_coach_email;
            """;
        command.Parameters.AddWithValue("subjectProfileId", subjectProfileId);

        var assignments = new List<CoachProfileAssignment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            assignments.Add(new CoachProfileAssignment(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        return assignments;
    }

    public async Task SaveAssignmentAsync(SaveCoachProfileAssignmentRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {TableName}
                (normalized_coach_email, coach_email, coach_actor_id, subject_profile_id, is_active, updated_at, updated_by)
            VALUES
                (@normalizedEmail, @coachEmail, NULL, @subjectProfileId, @isActive, @updatedAt, @updatedBy)
            ON CONFLICT (normalized_coach_email, subject_profile_id)
            DO UPDATE SET coach_email = EXCLUDED.coach_email,
                          is_active = EXCLUDED.is_active,
                          updated_at = EXCLUDED.updated_at,
                          updated_by = EXCLUDED.updated_by;
            """;
        command.Parameters.AddWithValue("normalizedEmail", NormalizeEmail(request.CoachEmail));
        command.Parameters.AddWithValue("coachEmail", request.CoachEmail.Trim());
        command.Parameters.AddWithValue("subjectProfileId", request.SubjectProfileId);
        command.Parameters.AddWithValue("isActive", request.IsActive);
        command.Parameters.AddWithValue("updatedAt", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("updatedBy", request.UpdatedBy);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private string TableName => $"{QuoteIdentifier(_options.Schema)}.{QuoteIdentifier("coach_profile_assignments")}";
    private static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();
    private static string QuoteIdentifier(string identifier) => new NpgsqlCommandBuilder().QuoteIdentifier(identifier);
}
