using Npgsql;
using NpgsqlTypes;

using PersonalAgent.Contracts.Coding;

namespace PersonalAgent.Integrations;

public sealed partial class CodingJobStore
{
    public async Task<CodingJob?> FindRequestAsync(string Actor, string Key, CancellationToken Token)
    {
        await using var Connection = await OpenAsync(Token);
        await using var Query = new NpgsqlCommand("SELECT payload,state,requests FROM app.coding_jobs WHERE actor=@actor AND request_key=@key", Connection);
        Query.Parameters.AddWithValue("actor", Actor);
        Query.Parameters.AddWithValue("key", Key);
        await using var Reader = await Query.ExecuteReaderAsync(Token);
        return await Reader.ReadAsync(Token) ? Read(Reader) : null;
    }

    public async Task<IReadOnlyList<CodingJob>> UnpostedResultsAsync(CancellationToken Token)
    {
        await using var Connection = await OpenAsync(Token);
        await using var Query = new NpgsqlCommand("""
            SELECT payload,state,requests FROM app.coding_jobs
            WHERE NOT result_posted AND payload::jsonb->>'sessionId' IS NOT NULL AND state IN ('PrOpened','Failed','Cancelled')
            ORDER BY deadline LIMIT 50
            """, Connection);
        await using var Reader = await Query.ExecuteReaderAsync(Token);
        var Jobs = new List<CodingJob>();
        while (await Reader.ReadAsync(Token)) Jobs.Add(Read(Reader));
        return Jobs;
    }

    public async Task MarkConversationPostAsync(Guid Id, bool Question, CancellationToken Token)
    {
        await using var Connection = await OpenAsync(Token);
        await using var Query = new NpgsqlCommand(Question
            ? "UPDATE app.coding_questions SET posted=true WHERE id=@id"
            : "UPDATE app.coding_jobs SET result_posted=true WHERE id=@id", Connection);
        Query.Parameters.AddWithValue("id", Id);
        await Query.ExecuteNonQueryAsync(Token);
    }

    public async Task<CodingQuestion?> AskAsync(Guid JobId, string Capability, AskCodingQuestion Request, CancellationToken Token)
    {
        await using var Connection = await OpenAsync(Token);
        await using var Query = new NpgsqlCommand("""
            WITH job AS (
                SELECT * FROM app.coding_jobs WHERE id=@job AND capability_hash=@hash AND state='Running'
                    AND deadline>now() AND payload::jsonb->>'sessionId' IS NOT NULL FOR UPDATE
            ), inserted AS (
                INSERT INTO app.coding_questions(id,job_id,question)
                SELECT @id,id,@text FROM job
                WHERE (SELECT count(*) FROM app.coding_questions WHERE job_id=@job) < 5
                ON CONFLICT DO NOTHING RETURNING *
            ), question AS (
                SELECT * FROM inserted UNION ALL
                SELECT * FROM app.coding_questions WHERE id=@id AND job_id=@job AND question=@text
            )
            SELECT q.id,q.job_id,(j.payload::jsonb->>'sessionId')::uuid,q.question,q.answer,j.deadline
            FROM question q JOIN job j ON j.id=q.job_id
            """, Connection);
        Query.Parameters.AddWithValue("job", JobId);
        Query.Parameters.AddWithValue("id", Request.Id);
        Query.Parameters.AddWithValue("hash", Hash(Capability));
        Query.Parameters.AddWithValue("text", Request.Text);
        await using var Reader = await Query.ExecuteReaderAsync(Token);
        return await Reader.ReadAsync(Token) ? ReadQuestion(Reader) : null;
    }

    public async Task<IReadOnlyList<CodingQuestion>> QuestionsAsync(string? Actor, string? Subject, Guid? SessionId, bool Unposted, CancellationToken Token)
    {
        await using var Connection = await OpenAsync(Token);
        await using var Query = new NpgsqlCommand("""
            SELECT q.id,q.job_id,(j.payload::jsonb->>'sessionId')::uuid,q.question,q.answer,j.deadline
            FROM app.coding_questions q JOIN app.coding_jobs j ON j.id=q.job_id
            WHERE (@unposted AND NOT q.posted OR NOT @unposted AND q.answer IS NULL AND j.state='Running' AND j.deadline>now()
                AND j.actor=@actor AND j.payload::jsonb->>'subject'=@subject)
              AND (@session IS NULL OR j.payload::jsonb->>'sessionId'=@session)
            ORDER BY j.deadline,q.id LIMIT 50
            """, Connection);
        Query.Parameters.AddWithValue("unposted", Unposted);
        Query.Parameters.AddWithValue("actor", NpgsqlDbType.Text, (object?)Actor ?? DBNull.Value);
        Query.Parameters.AddWithValue("subject", NpgsqlDbType.Text, (object?)Subject ?? DBNull.Value);
        Query.Parameters.AddWithValue("session", NpgsqlDbType.Text, (object?)SessionId?.ToString("D") ?? DBNull.Value);
        await using var Reader = await Query.ExecuteReaderAsync(Token);
        var Questions = new List<CodingQuestion>();
        while (await Reader.ReadAsync(Token)) Questions.Add(ReadQuestion(Reader));
        return Questions;
    }

    public async Task<bool> AnswerAsync(Guid Id, string Actor, string Subject, Guid? SessionId, string Answer, CancellationToken Token)
    {
        await using var Connection = await OpenAsync(Token);
        await using var Query = new NpgsqlCommand("""
            WITH job AS (
                SELECT j.id FROM app.coding_jobs j JOIN app.coding_questions q ON q.job_id=j.id
                WHERE q.id=@id AND j.actor=@actor AND j.payload::jsonb->>'subject'=@subject
                  AND (@session IS NULL OR j.payload::jsonb->>'sessionId'=@session)
                  AND j.state='Running' AND j.deadline>now() FOR UPDATE OF j
            )
            UPDATE app.coding_questions q SET answer=@answer FROM job j
            WHERE q.id=@id AND q.job_id=j.id AND (q.answer IS NULL OR q.answer=@answer)
            """, Connection);
        Query.Parameters.AddWithValue("id", Id);
        Query.Parameters.AddWithValue("actor", Actor);
        Query.Parameters.AddWithValue("subject", Subject);
        Query.Parameters.AddWithValue("session", NpgsqlDbType.Text, (object?)SessionId?.ToString("D") ?? DBNull.Value);
        Query.Parameters.AddWithValue("answer", Answer);
        return await Query.ExecuteNonQueryAsync(Token) == 1;
    }

    private static CodingQuestion ReadQuestion(NpgsqlDataReader Reader) =>
        new(Reader.GetGuid(0), Reader.GetGuid(1), Reader.GetGuid(2), Reader.GetString(3),
            Reader.IsDBNull(4) ? null : Reader.GetString(4), Reader.GetFieldValue<DateTimeOffset>(5));
}
