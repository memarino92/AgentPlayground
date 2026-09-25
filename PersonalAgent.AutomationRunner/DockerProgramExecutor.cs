using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PersonalAgent.Contracts.Automations;

namespace PersonalAgent.AutomationRunner;

public sealed record ProgramResult(string Output, AutomationProgramEvidence Evidence);

/// <summary>Only this dedicated host has Docker access. Source/input travel over stdin, never shell arguments or host mounts.</summary>
public sealed class DockerProgramExecutor
{
    private string? ImageId;
    private readonly SemaphoreSlim Capacity = new(2, 2);
    private const string Label = "personalagent.automation-sandbox=true";

    public async Task InitializeAsync(CancellationToken Token)
    {
        var Result = await DockerAsync(["image", "inspect", "--format", "{{.Id}}", AutomationPrograms.SandboxImage], null, Token);
        var Id = Result.Output.Trim();
        if (Result.ExitCode != 0 || !Regex.IsMatch(Id, "^sha256:[a-f0-9]{64}$"))
            throw new InvalidOperationException("Build the versioned automation sandbox image before starting the runner.");
        ImageId = Id; // Immutable identity for this runner lifetime. Never pull/build agent-selected images.
    }

    public async Task<ProgramResult> ExecuteAsync(string Source, string Input, CancellationToken Token)
    {
        if (ImageId is null) throw new InvalidOperationException("Sandbox is not initialized.");
        if (string.IsNullOrWhiteSpace(Source) || Source.Length > AutomationPrograms.MaxSource || Input.Length > AutomationPrograms.MaxInput)
            throw new ArgumentException("Invalid program source/input size.");
        await Capacity.WaitAsync(Token);
        var Name = "automation-program-" + Guid.NewGuid().ToString("N");
        var Started = Stopwatch.GetTimestamp();
        var Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Source)));
        using var Deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Deadline.CancelAfter(TimeSpan.FromSeconds(AutomationPrograms.DeadlineSeconds));
        try
        {
            using var Archive = new MemoryStream();
            using (var Tar = new TarWriter(Archive, leaveOpen: true))
            {
                AddFile(Tar, "Program.cs", Source);
                AddFile(Tar, "input.txt", Input);
            }
            var Arguments = CreateArguments(Name, ImageId);
            var Result = await DockerAsync(Arguments, Archive.ToArray(), Deadline.Token);
            var Status = Result.ExitCode == 0 ? "Completed" : Result.ExitCode == 200 ? "BuildFailed" : "ExecutionFailed";
            return new(Result.Output, new(Hash, ImageId, Result.ExitCode, Status, Result.Error, Stopwatch.GetElapsedTime(Started).TotalSeconds,
                Status == "Completed" ? "" : Result.Output));
        }
        catch (OutputLimitException)
        {
            return new("", new(Hash, ImageId, null, "OutputLimitExceeded", "Combined output exceeded 32 KiB.", Stopwatch.GetElapsedTime(Started).TotalSeconds));
        }
        catch (OperationCanceledException) when (!Token.IsCancellationRequested)
        {
            return new("", new(Hash, ImageId, null, "TimedOut", "Build and execution exceeded 90 seconds.", Stopwatch.GetElapsedTime(Started).TotalSeconds));
        }
        finally
        {
            // Killing the CLI alone does not kill its container. Always remove it via a fresh deadline.
            using var Cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await DockerAsync(["rm", "--force", Name], null, Cleanup.Token); }
            finally { Capacity.Release(); }
        }
    }

    public static string[] CreateArguments(string Name, string Image) =>
    [
        "run", "--rm", "--interactive", "--name", Name, "--label", Label, "--pull=never",
        "--network=none", "--read-only", "--user=65532:65532", "--cap-drop=ALL", "--security-opt=no-new-privileges",
        "--memory=512m", "--memory-swap=512m", "--cpus=1", "--pids-limit=128", "--ulimit", "nofile=256:256",
        "--log-driver=none", "--tmpfs", "/work:rw,noexec,nosuid,size=134217728,mode=1777",
        "--tmpfs", "/tmp:rw,noexec,nosuid,size=16777216,mode=1777", Image
    ];

    private static void AddFile(TarWriter Writer, string Name, string Content)
    {
        using var Data = new MemoryStream(Encoding.UTF8.GetBytes(Content));
        Writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, Name) { DataStream = Data,
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead });
    }

    private static async Task<(int ExitCode, string Output, string Error)> DockerAsync(string[] Arguments, byte[]? Input, CancellationToken Token)
    {
        using var Process = new Process { StartInfo = new("docker") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var Argument in Arguments) Process.StartInfo.ArgumentList.Add(Argument);
        if (!Process.Start()) throw new InvalidOperationException("Docker did not start.");
        var Count = 0;
        async Task<string> ReadAsync(StreamReader Reader)
        {
            var Text = new StringBuilder();
            var Buffer = new char[1024];
            int Read;
            while ((Read = await Reader.ReadAsync(Buffer.AsMemory(), Token)) > 0)
            {
                if (Interlocked.Add(ref Count, Read) > AutomationPrograms.MaxOutput)
                {
                    if (!Process.HasExited) Process.Kill(entireProcessTree: true);
                    throw new OutputLimitException();
                }
                Text.Append(Buffer, 0, Read);
            }
            return Text.ToString();
        }
        var Output = ReadAsync(Process.StandardOutput);
        var Error = ReadAsync(Process.StandardError);
        try
        {
            if (Input is not null) await Process.StandardInput.BaseStream.WriteAsync(Input, Token);
            Process.StandardInput.Close();
            await Process.WaitForExitAsync(Token);
            await Task.WhenAll(Output, Error);
            return (Process.ExitCode, await Output, await Error);
        }
        finally
        {
            if (!Process.HasExited) Process.Kill(entireProcessTree: true);
            // Observe both reader tasks on cancellation/broken pipes as well.
            try { await Task.WhenAll(Output, Error); } catch when (Token.IsCancellationRequested) { }
        }
    }

    private sealed class OutputLimitException : Exception;
}
