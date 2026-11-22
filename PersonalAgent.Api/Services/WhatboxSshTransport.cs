using System.Text;

using Renci.SshNet;
using Renci.SshNet.Common;

namespace PersonalAgent.Api.Services;

internal enum WhatboxOperation { Connection, DiskUsage, SeedingStatus }

internal interface IWhatboxSshTransport
{
    Task<string> ExecuteAsync(WhatboxConfiguration Configuration, WhatboxOperation Operation, CancellationToken Token);
}

internal sealed class WhatboxSshTransport : IWhatboxSshTransport
{
    public async Task<string> ExecuteAsync(WhatboxConfiguration Configuration, WhatboxOperation Operation, CancellationToken Token)
    {
        var commandText = Operation switch
        {
            WhatboxOperation.Connection => "true",
            WhatboxOperation.DiskUsage => PythonCommand(Configuration.FilesDirectory, DiskCommand),
            WhatboxOperation.SeedingStatus => PythonCommand(Configuration.RTorrentSocketPath, SeedingCommand),
            _ => throw new ArgumentOutOfRangeException(nameof(Operation))
        };
        using var key = ParseKey(Configuration.PrivateKey!);
        using var authentication = new PrivateKeyAuthenticationMethod(Configuration.Username, key);
        using var client = new SshClient(new Renci.SshNet.ConnectionInfo(Configuration.Host, 22, Configuration.Username, authentication)
        {
            Timeout = TimeSpan.FromSeconds(8)
        });
        var rejectedHost = false;
        client.HostKeyReceived += (_, args) =>
        {
            args.CanTrust = string.Equals(args.FingerPrintSHA256.TrimEnd('='), Configuration.HostKeySha256, StringComparison.Ordinal);
            rejectedHost = !args.CanTrust;
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await client.ConnectAsync(deadline.Token);
            using var command = client.CreateCommand(commandText);
            command.CommandTimeout = TimeSpan.FromSeconds(24);
            await command.ExecuteAsync(deadline.Token);
            if (command.ExitStatus != 0 || command.OutputStream.Length > 4096)
                throw new WhatboxOperationException(Operation == WhatboxOperation.SeedingStatus
                    ? "rTorrent status is unavailable. Check that rTorrent is running and its local SCGI socket is present."
                    : "The Whatbox command did not return a valid result.");
            return command.Result;
        }
        catch (SshAuthenticationException)
        {
            throw new WhatboxOperationException("Whatbox rejected the SSH key. Check the username and authorized public key.");
        }
        catch (Exception error) when (!Token.IsCancellationRequested &&
            error is SshException or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            throw new WhatboxOperationException(rejectedHost
                ? "The Whatbox SSH host key did not match the saved fingerprint."
                : "The SSH operation failed or timed out. Check connectivity from the API host and try again.");
        }
    }

    internal static PrivateKeyFile ParseKey(string PrivateKey)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(PrivateKey));
        return new PrivateKeyFile(stream);
    }

    private static string PythonCommand(string Path, string Script)
    {
        // The encoded argument uses only shell-safe ASCII. Python handles the decoded path as data.
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(Path));
        return $"python3 - {encoded} <<'PERSONAL_AGENT_WHATBOX'\n{Script}\nPERSONAL_AGENT_WHATBOX\n";
    }

    internal const string DiskCommand = """
        import base64, json, os, subprocess, sys

        path = os.path.realpath(os.path.expanduser(base64.b64decode(sys.argv[1]).decode()))
        measured = subprocess.check_output(["du", "-sxB1", "--", path], timeout=20)
        stats = os.statvfs(path)
        print(json.dumps({
            "filesAllocatedBytes": int(measured.split(b"\t", 1)[0]),
            "filesystemAvailableBytes": stats.f_bavail * stats.f_frsize,
            "filesystemTotalBytes": stats.f_blocks * stats.f_frsize
        }))
        """;

    internal const string SeedingCommand = """
        import base64, json, os, signal, socket, sys, xmlrpc.client

        signal.alarm(20)
        limit = 16 * 1024 * 1024
        body = xmlrpc.client.dumps(
            ("", "main", "d.complete=", "d.is_active=", "d.up.rate="),
            methodname="d.multicall2").encode()
        headers = f"CONTENT_LENGTH\0{len(body)}\0SCGI\x001\0".encode()
        with socket.socket(socket.AF_UNIX) as connection:
            connection.settimeout(8)
            connection.connect(os.path.expanduser(base64.b64decode(sys.argv[1]).decode()))
            connection.sendall(str(len(headers)).encode() + b":" + headers + b"," + body)
            with connection.makefile("rb") as response:
                payload = response.read(limit + 1)
        if len(payload) > limit:
            raise ValueError("SCGI response exceeded limit")
        header, separator, xml = payload.partition(b"\r\n\r\n")
        if not separator:
            header, separator, xml = payload.partition(b"\n\n")
        if not separator:
            raise ValueError("Missing SCGI response header")
        rows = xmlrpc.client.loads(xml)[0][0]
        seeding = sum(bool(complete and active) for complete, active, rate in rows)
        complete = sum(bool(complete) for complete, active, rate in rows)
        print(json.dumps({
            "total": len(rows),
            "seeding": seeding,
            "completeInactive": complete - seeding,
            "incomplete": len(rows) - complete,
            "seedingUploadBytesPerSecond": sum(rate for complete, active, rate in rows if complete and active)
        }))
        """;
}
