using System.Globalization;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace PersonalAgent.Backup;

public static partial class BackupUpload
{
    public static void Run(Func<ISftpClient> CreateClient, Stream Archive, string Remote, BackupDiagnostics Diagnostics)
    {
        var temporary = Remote + ".partial";
        var uploaded = false;
        var uploadStarted = false;
        using var client = CreateClient();
        try
        {
            Diagnostics.Begin("connecting to SFTP");
            client.Connect();
            Diagnostics.Begin("preparing the SFTP backup directory");
            var current = "";
            foreach (var segment in Remote[..Remote.LastIndexOf('/')].Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current += "/" + segment;
                if (!client.Exists(current))
                {
                    client.CreateDirectory(current);
                    Diagnostics.Info($"Created SFTP directory {current}");
                }
                if (!client.GetAttributes(current).IsDirectory)
                    throw new InvalidOperationException("The SFTP backup path contains a file instead of a directory.");
            }
            Diagnostics.Begin("cleaning abandoned SFTP uploads");
            var directory = Remote[..Remote.LastIndexOf('/')];
            if (directory.Length == 0) directory = "/";
            CleanupAbandoned(client, directory, DateTime.UtcNow, Diagnostics);
            Diagnostics.Begin("uploading the database dump");
            var progress = System.Diagnostics.Stopwatch.StartNew();
            ulong transferred = 0;
            var length = Archive.Length;
            Diagnostics.Context("archive_bytes", length);
            Diagnostics.Context("remote_archive", Remote);
            Diagnostics.Info($"Uploading {length} bytes to {temporary}");
            uploadStarted = true;
            client.UploadFile(Archive, temporary, canOverride: false, Bytes =>
            {
                transferred = Bytes;
                Diagnostics.Context("uploaded_bytes", Bytes);
                if (progress.Elapsed < TimeSpan.FromSeconds(30)) return;
                Diagnostics.Info($"Uploaded {Bytes}/{length} bytes ({100.0 * Bytes / length:F1}%)");
                progress.Restart();
            });
            if (client.GetAttributes(temporary).Size != length)
                throw new InvalidOperationException("Remote archive size does not match the local dump.");
            Diagnostics.Info($"Upload complete: {transferred}/{length} bytes; remote size verified");
            Diagnostics.Begin("finalizing the SFTP upload");
            client.RenameFile(temporary, Remote);
            uploaded = true;
            Diagnostics.Info($"Finalized archive {Remote}");
        }
        finally
        {
            if (uploadStarted && !uploaded)
            {
                // A lost connection can still claim IsConnected; always use a fresh, pinned connection.
                try
                {
                    using var cleanup = CreateClient();
                    cleanup.Connect();
                    if (cleanup.Exists(temporary)) cleanup.DeleteFile(temporary);
                    Diagnostics.Info($"Removed failed upload {temporary}");
                }
                catch (Exception exception)
                {
                    Diagnostics.Failure(exception, $"Could not remove failed upload {temporary}; manual cleanup required", 2);
                }
            }
        }
    }

    public static void CleanupAbandoned(ISftpClient Client, string Directory, DateTime Now, BackupDiagnostics Diagnostics)
    {
        var cutoff = Now.AddDays(-1);
        try
        {
            foreach (var file in Client.ListDirectory(Directory))
            {
                var match = PartialName().Match(file.Name);
                if (!file.IsRegularFile || file.IsSymbolicLink || !match.Success || file.LastWriteTimeUtc >= cutoff) continue;
                if (!DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-ddTHH-mm-ss'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created) || created >= cutoff) continue;
                // Construct the path from the configured directory and validated name, never server-supplied FullName.
                var path = $"{Directory.TrimEnd('/')}/{file.Name}";
                try
                {
                    Client.DeleteFile(path);
                    Diagnostics.Info($"Removed abandoned partial upload {path}");
                }
                catch (Exception exception) { Diagnostics.Failure(exception, $"Could not remove abandoned partial upload {path}", 2); }
            }
        }
        catch (Exception exception) { Diagnostics.Failure(exception, "Could not inspect abandoned partial uploads; continuing backup", 2); }
    }

    [GeneratedRegex(@"\Aagentplayground-(\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}Z)-[0-9a-f]{32}\.dump\.partial\z", RegexOptions.CultureInvariant)]
    private static partial Regex PartialName();
}
