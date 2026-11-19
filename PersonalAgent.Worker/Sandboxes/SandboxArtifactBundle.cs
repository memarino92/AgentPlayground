using System.Formats.Tar;
using System.IO.Compression;

using PersonalAgent.Contracts.Automations;

namespace PersonalAgent.Worker.Sandboxes;

internal sealed record SandboxArtifactBundle(byte[] Zip, IReadOnlyList<SandboxArtifact> Files)
{
    internal static async Task<SandboxArtifactBundle?> ReadAsync(string Source, CancellationToken Token)
    {
        using var Input = File.OpenRead(Source);
        if (Input.Length > 72L * 1024 * 1024) throw new InvalidDataException("Export archive exceeds its transfer budget.");
        using var Tar = new TarReader(Input);
        using var Output = new MemoryStream();
        var Files = new List<SandboxArtifact>();
        var Paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long Total = 0;
        using (var Zip = new ZipArchive(Output, ZipArchiveMode.Create, leaveOpen: true))
        {
            while (await Tar.GetNextEntryAsync(cancellationToken: Token) is { } Entry)
            {
                var Directory = Entry.EntryType == TarEntryType.Directory;
                if (!Directory && Entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    throw new InvalidDataException("Artifacts must be regular files or directories.");
                var Name = Entry.Name.StartsWith("./", StringComparison.Ordinal) ? Entry.Name[2..] : Entry.Name;
                if (Directory) Name = Name.TrimEnd('/');
                if (Directory && Name is "" or ".") continue;
                if (Name.Length > 1024 || Name.Contains('\\') || Name.Contains(':') || Name.Any(char.IsControl)
                    || Name.Split('/').Any(Part => Part is "" or "." or "..") || !Paths.Add(Name))
                    throw new InvalidDataException("Invalid or duplicate artifact path.");
                if (Paths.Count > 2048 || (!Directory && Files.Count >= 512) || Entry.Length > 64L * 1024 * 1024 - Total)
                    throw new InvalidDataException("Artifact export exceeds its file count or size budget.");
                var Member = Zip.CreateEntry(Directory ? Name + "/" : Name, CompressionLevel.Fastest);
                Member.ExternalAttributes = ((Directory ? 0x4000 : 0x8000) | ((int)Entry.Mode & 0x1ff)) << 16;
                if (Directory) continue;
                Total += Entry.Length;
                Files.Add(new(Name, Entry.Length));
                using var Target = Member.Open();
                if (Entry.DataStream is not null) await Entry.DataStream.CopyToAsync(Target, Token);
            }
        }
        return Files.Count == 0 ? null : new(Output.ToArray(), Files);
    }
}
