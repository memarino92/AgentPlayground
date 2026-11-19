using System.IO.Compression;
using Npgsql;
using PersonalAgent.Contracts.Automations;

namespace PersonalAgent.Integrations;

public sealed class SandboxInputStore(IntegrationDatabase Database)
{
    public async Task<Guid> UploadAsync(string Actor, string Profile, byte[] Bytes, CancellationToken Token)
    {
        if (Bytes.Length > SandboxInputFile.MaxBytes) throw new ArgumentException("Maximum upload is 16 MiB.");
        var Id = Guid.NewGuid();
        await using var Connection = await Database.OpenAsync(Token);
        await using var Insert = new NpgsqlCommand("INSERT INTO app.sandbox_uploads(id,actor,profile,content) VALUES ($1,$2,$3,$4)", Connection);
        Insert.Parameters.AddWithValue(Id);
        Insert.Parameters.AddWithValue(Actor);
        Insert.Parameters.AddWithValue(Profile);
        Insert.Parameters.AddWithValue(Bytes);
        await Insert.ExecuteNonQueryAsync(Token);
        return Id;
    }

    // Derive the recipient identity from the durable run. File references never grant access by themselves.
    public async Task<byte[]> ReadAsync(Guid Recipient, SandboxInputFile File, int Remaining, CancellationToken Token)
    {
        SandboxInputFile.Validate([File]);
        var Parts = File.Reference.Split(':');
        var Upload = Parts[0] == "attachment";
        const string RecipientScope = """
            FROM automation."Runs" r JOIN automation."Definitions" d ON d."Id"=r."AutomationId"
            """;
        var Sql = Upload ? "SELECT u.content " + RecipientScope + """
             JOIN app.sandbox_uploads u ON u.actor=d."ActorId" AND u.profile=d."SubjectProfileId"
            WHERE r."CorrelationId"=$1 AND u.id=$2 AND octet_length(u.content)<=$3
            """ : "SELECT a.artifact_zip " + RecipientScope + """
             JOIN automation."Definitions" src ON src."SubjectProfileId"=d."SubjectProfileId" AND src."ActorId"=d."ActorId"
            JOIN automation."Runs" prev ON prev."AutomationId"=src."Id"
            JOIN automation."Steps" step ON step."RunId"=prev."CorrelationId" AND step."Status"='Completed'
            JOIN app.automation_sandboxes a ON a.run_id=step."RunId" AND a.step=step."Index"
            WHERE r."CorrelationId"=$1 AND prev."CorrelationId"=$2 AND a.step=$3
            """;
        await using var Connection = await Database.OpenAsync(Token);
        await using var Query = new NpgsqlCommand(Sql, Connection);
        Query.Parameters.AddWithValue(Recipient);
        Query.Parameters.AddWithValue(Guid.Parse(Parts[1]));
        Query.Parameters.AddWithValue(Upload ? Remaining : int.Parse(Parts[2]));
        if (await Query.ExecuteScalarAsync(Token) is not byte[] Data) throw new KeyNotFoundException("Input file unavailable.");
        if (Upload) return Data;
        using var Archive = new ZipArchive(new MemoryStream(Data));
        var Entry = Archive.Entries.Where(E => E.Name.Length > 0).ElementAtOrDefault(int.Parse(Parts[3]));
        if (Entry is null || Entry.Length > Remaining) throw new KeyNotFoundException("Input file unavailable.");
        using var Input = Entry.Open();
        var Bytes = new byte[(int)Entry.Length];
        await Input.ReadExactlyAsync(Bytes, Token);
        return Bytes;
    }
}
