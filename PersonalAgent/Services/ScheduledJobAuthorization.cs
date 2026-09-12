using AgentPlayground.Contracts.Configuration;
using AgentPlayground.Integrations;
using Npgsql;
using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal interface IScheduledActorPolicy
{
    Task<string?> ResolveRoleAsync(string ActorId, string? Email, CancellationToken CancellationToken);
}

// Read the database policy on every check; a signed or stored role is not a durable grant.
internal sealed class DatabaseScheduledActorPolicy(IntegrationDatabase Database) : IScheduledActorPolicy
{
    public async Task<string?> ResolveRoleAsync(string ActorId, string? Email, CancellationToken CancellationToken)
    {
        await using var Connection = await Database.OpenAsync(CancellationToken);
        await using var Command = new NpgsqlCommand("""
            SELECT scope, key, value, is_secret FROM app.configuration_settings
            WHERE scope IN ('Shared', 'Web') AND is_active AND lower(key) IN
                ('authentication:schemes:github:allowedusers', 'authentication:schemes:google:allowedemails')
            ORDER BY CASE WHEN scope = 'Shared' THEN 0 ELSE 1 END
            """, Connection);
        var Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var Reader = await Command.ExecuteReaderAsync(CancellationToken);
        while (await Reader.ReadAsync(CancellationToken))
            Values[Reader.GetString(1)] = Reader.GetBoolean(3)
                ? PostgresConfigurationCrypto.Decrypt(Reader.GetString(2), Reader.GetString(0), Reader.GetString(1), Database.EncryptionKey)
                : Reader.GetString(2);
        bool Contains(string Key, string? Value) => Value is not null && Values.TryGetValue(Key, out var List)
            && List.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(Value, StringComparer.OrdinalIgnoreCase);
        if (!ActorId.StartsWith("google:", StringComparison.OrdinalIgnoreCase)
            && Contains("Authentication:Schemes:GitHub:AllowedUsers", ActorId)) return AgentRoles.Owner;
        return ActorId.StartsWith("google:", StringComparison.Ordinal) && Contains("Authentication:Schemes:Google:AllowedEmails", Email)
            ? AgentRoles.Coach : null;
    }
}

internal sealed class ScheduledJobAuthorization(IScheduledActorPolicy Policy, ICoachAssignmentStore Assignments, ToolAccessService Tools)
{
    public async Task<AgentAccessContext?> ResolveAsync(string? ActorId, string? Email, string Subject, CancellationToken CancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ActorId)) return null;
        var Role = await Policy.ResolveRoleAsync(ActorId, Email, CancellationToken);
        if (Role is null) return null;
        var Allowed = Role == AgentRoles.Owner
            ? string.Equals(ActorId, Subject, StringComparison.OrdinalIgnoreCase)
            : !string.IsNullOrWhiteSpace(Email) && (await Assignments.GetAssignedProfilesAsync(ActorId, Email, CancellationToken)).Contains(Subject, StringComparer.OrdinalIgnoreCase);
        return Allowed ? new AgentAccessContext(ActorId, Role, Subject) { Email = Email } : null;
    }

    public async Task<AgentAccessContext?> ForExecutionAsync(ScheduledJob Job, CancellationToken CancellationToken)
    {
        var Access = await ResolveAsync(Job.ActorId, Job.ActorEmail, Job.SubjectProfileId, CancellationToken);
        var Tool = Job.JobType switch
        {
            "AgentTask" => AgentToolKeys.ScheduleAgentTask,
            "Notification" => AgentToolKeys.ScheduleNotification,
            _ => null
        };
        return Access is not null && Tool is not null && await Tools.IsAllowedAsync(Access.Role, Tool, CancellationToken)
            ? Access with { ScheduledTaskId = Job.TaskId, SessionId = Job.SessionId } : null;
    }
}
