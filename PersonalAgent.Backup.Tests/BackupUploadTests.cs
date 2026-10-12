using System.Reflection;
using Moq;
using PersonalAgent.Backup;
using PersonalAgent.Integrations;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using Xunit;

namespace PersonalAgent.Backup.Tests;

public class BackupUploadTests
{
    private const string Remote = "/agentplayground-test.dump";
    private const string Partial = Remote + ".partial";

    [Fact]
    public void InterruptedUploadReconnectsAndRemovesOnlyItsOwnPartial()
    {
        var upload = new Mock<ISftpClient>();
        var cleanup = new Mock<ISftpClient>();
        var failure = new IOException("SSH connection lost");
        upload.Setup(Client => Client.UploadFile(It.IsAny<Stream>(), Partial, false, It.IsAny<Action<ulong>>())).Throws(failure);
        cleanup.Setup(Client => Client.Exists(Partial)).Returns(true);
        var clients = new Queue<ISftpClient>([upload.Object, cleanup.Object]);
        using var stream = new MemoryStream([1, 2, 3]);

        Assert.Same(failure, Assert.Throws<IOException>(() => BackupUpload.Run(clients.Dequeue, stream, Remote, new())));

        cleanup.Verify(Client => Client.Connect(), Times.Once);
        cleanup.Verify(Client => Client.DeleteFile(Partial), Times.Once);
        cleanup.Verify(Client => Client.DeleteFile(Remote), Times.Never);
        upload.Verify(Client => Client.RenameFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void CleanupFailureIsReportedWithoutReplacingTheOriginalFailure()
    {
        var upload = new Mock<ISftpClient>();
        var cleanup = new Mock<ISftpClient>();
        var errors = new Mock<IIntegrationErrorClient>();
        var failure = new IOException("SSH upload disconnected");
        upload.Setup(Client => Client.UploadFile(It.IsAny<Stream>(), Partial, false, It.IsAny<Action<ulong>>())).Throws(failure);
        cleanup.Setup(Client => Client.Connect()).Throws(new IOException("SSH reconnect refused"));
        var clients = new Queue<ISftpClient>([upload.Object, cleanup.Object]);
        using var stream = new MemoryStream([1]);
        var diagnostics = new BackupDiagnostics { ErrorClient = errors.Object };

        Assert.Same(failure, Assert.Throws<IOException>(() => BackupUpload.Run(clients.Dequeue, stream, Remote, diagnostics)));

        errors.Verify(Client => Client.Capture(It.Is<IntegrationError>(Error => Error.EventId == 2
            && Error.Message!.Contains("manual cleanup required") && Error.ExceptionMessage == "SSH reconnect refused")), Times.Once);
    }

    [Fact]
    public void ConnectionFailureDoesNotDeleteAnything()
    {
        var client = new Mock<ISftpClient>();
        client.Setup(Client => Client.Connect()).Throws(new IOException("SSH handshake failed"));
        var created = 0;
        using var stream = new MemoryStream([1]);

        Assert.Throws<IOException>(() => BackupUpload.Run(() => { created++; return client.Object; }, stream, Remote, new()));

        Assert.Equal(1, created);
        client.Verify(Client => Client.DeleteFile(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void SizeAndRenameFailuresNeverLeaveThisRunsPartial(long RemoteSize, bool RenameFails)
    {
        var upload = new Mock<ISftpClient>();
        var cleanup = new Mock<ISftpClient>();
        // SSH.NET attributes have an internal wire-format constructor.
        var attributes = (SftpFileAttributes)Activator.CreateInstance(typeof(SftpFileAttributes),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [DateTime.UtcNow, DateTime.UtcNow, RemoteSize, 0, 0, (uint)0x8000, null!], null)!;
        upload.Setup(Client => Client.GetAttributes(Partial)).Returns(attributes);
        if (RenameFails) upload.Setup(Client => Client.RenameFile(Partial, Remote)).Throws(new IOException("rename refused"));
        cleanup.Setup(Client => Client.Exists(Partial)).Returns(true);
        var clients = new Queue<ISftpClient>([upload.Object, cleanup.Object]);
        using var stream = new MemoryStream([1, 2, 3]);

        if (RemoteSize != 3 || RenameFails)
        {
            Assert.ThrowsAny<Exception>(() => BackupUpload.Run(clients.Dequeue, stream, Remote, new()));
            cleanup.Verify(Client => Client.DeleteFile(Partial), Times.Once);
        }
        else
        {
            BackupUpload.Run(clients.Dequeue, stream, Remote, new());
            Assert.Single(clients);
            upload.Verify(Client => Client.RenameFile(Partial, Remote), Times.Once);
        }
        cleanup.Verify(Client => Client.DeleteFile(Remote), Times.Never);
    }

    [Fact]
    public void AbandonedCleanupPreservesCompletedRecentUnrelatedAndSymlinkFiles()
    {
        var now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        var name = "agentplayground-2026-10-01T03-00-00Z-" + new string('a', 32) + ".dump";
        ISftpFile File(string Name, DateTime Modified, bool Symlink = false)
        {
            var file = new Mock<ISftpFile>();
            file.SetupGet(File => File.Name).Returns(Name);
            file.SetupGet(File => File.LastWriteTimeUtc).Returns(Modified);
            file.SetupGet(File => File.IsRegularFile).Returns(true);
            file.SetupGet(File => File.IsSymbolicLink).Returns(Symlink);
            return file.Object;
        }
        var client = new Mock<ISftpClient>();
        client.Setup(Client => Client.ListDirectory("/backups", null)).Returns([
            File(name + ".partial", now.AddDays(-2)), File(name, now.AddDays(-2)),
            File(name.Replace(new string('a', 32), new string('b', 32)) + ".partial", now),
            File("unrelated.partial", now.AddDays(-2)),
            File(name.Replace("2026-10-01", "2026-10-11") + ".partial", now.AddDays(-2)),
            File(name.Replace(new string('a', 32), new string('c', 32)) + ".partial", now.AddDays(-2), true)]);

        BackupUpload.CleanupAbandoned(client.Object, "/backups", now, new());

        client.Verify(Client => Client.DeleteFile("/backups/" + name + ".partial"), Times.Once);
        client.Verify(Client => Client.DeleteFile(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void DiagnosticsRetainSshDetailsAndMaskKnownSecretsBeforeReporting()
    {
        var errors = new Mock<IIntegrationErrorClient>();
        var diagnostics = new BackupDiagnostics { ErrorClient = errors.Object };
        diagnostics.Protect("unlabeled-sensitive-value");
        diagnostics.Begin("uploading the database dump");
        diagnostics.Context("uploaded_bytes", 42);
        diagnostics.Failure(new IOException("SSH disconnected: unlabeled-sensitive-value; Password=other-secret"), "Database backup failed");

        errors.Verify(Client => Client.Capture(It.Is<IntegrationError>(Error =>
            Error.ExceptionDetails!.Contains("SSH disconnected")
            && !Error.ExceptionDetails.Contains("unlabeled-sensitive-value")
            && !Error.ExceptionDetails.Contains("other-secret")
            && Error.Properties!["stage"] == "uploading the database dump"
            && Error.Properties["uploaded_bytes"] == "42"
            && Error.Properties.ContainsKey("run_id"))), Times.Once);
    }
}
