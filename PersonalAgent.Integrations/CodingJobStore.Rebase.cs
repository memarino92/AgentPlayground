using Npgsql;

namespace PersonalAgent.Integrations;

public sealed partial class CodingJobStore
{
    public async Task<string?> GetRebaseCandidateAsync(Guid Id, CancellationToken Token)
    {
        await using var Connection = await OpenAsync(Token);
        await using var Query = new NpgsqlCommand("SELECT rebase_candidate FROM app.coding_jobs WHERE id=@id", Connection);
        Query.Parameters.AddWithValue("id", Id);
        return await Query.ExecuteScalarAsync(Token) as string;
    }

    public async Task<bool> SaveRebaseCandidateAsync(Guid Id, string Sha, CancellationToken Token)
    {
        await using var Connection = await OpenAsync(Token);
        await using var Query = new NpgsqlCommand("""
            UPDATE app.coding_jobs SET rebase_candidate=@sha
            WHERE id=@id AND state='Publishing' AND (rebase_candidate IS NULL OR rebase_candidate=@sha)
            """, Connection);
        Query.Parameters.AddWithValue("id", Id);
        Query.Parameters.AddWithValue("sha", Sha);
        return await Query.ExecuteNonQueryAsync(Token) == 1;
    }
}
