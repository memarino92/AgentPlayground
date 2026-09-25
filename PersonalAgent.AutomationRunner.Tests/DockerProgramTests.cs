using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using PersonalAgent.Contracts.Automations;
using Xunit;

namespace PersonalAgent.AutomationRunner.Tests;

public sealed class DockerProgramTests
{
    private static async Task<DockerProgramExecutor> ExecutorAsync()
    {
        var Executor = new DockerProgramExecutor();
        await Executor.InitializeAsync(default);
        return Executor;
    }

    [Fact]
    public async Task BuildsSingleFileAndReadsExplicitInput_WithImageAndSourceEvidence()
    {
        const string Source = "Console.Write(Console.In.ReadToEnd().ToUpperInvariant());";
        var Result = await (await ExecutorAsync()).ExecuteAsync(Source, "input with 'quotes' and $(commands)", default);
        Result.Evidence.Status.Should().Be("Completed", Result.Evidence.StandardError);
        Result.Output.Should().Be("INPUT WITH 'QUOTES' AND $(COMMANDS)");
        Result.Evidence.SourceHash.Should().Be(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Source))));
        Result.Evidence.ImageId.Should().StartWith("sha256:");
        Result.Evidence.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task InvalidSourceReturnsBoundedBuildDiagnostics()
    {
        var Result = await (await ExecutorAsync()).ExecuteAsync("this does not compile", "", default);
        Result.Evidence.Status.Should().Be("BuildFailed");
        Result.Evidence.StandardError.Should().Contain("error CS");
    }

    [Fact]
    public async Task NonzeroExitPreservesOutputAndRuntimeDiagnostics()
    {
        var Result = await (await ExecutorAsync()).ExecuteAsync("Console.Write(\"partial result\"); Console.Error.Write(\"failed calculation\"); return 7;", "", default);
        Result.Evidence.Status.Should().Be("ExecutionFailed");
        Result.Evidence.ExitCode.Should().Be(7);
        Result.Evidence.StandardError.Should().Be("failed calculation");
        Result.Evidence.StandardOutput.Should().Be("partial result");
    }

    [Fact]
    public async Task SandboxHasNoCredentialsOrNetworkAndCannotWriteRoot()
    {
        var Source = """
            using System.Net.Sockets;
            if (Environment.GetEnvironmentVariable("DATABASE_URL") is not null) throw new Exception("credential leaked");
            if (Environment.GetEnvironmentVariable("CONFIG_ENCRYPTION_KEY") is not null) throw new Exception("key leaked");
            if (File.Exists("/var/run/docker.sock")) throw new Exception("socket exposed");
            var Status = File.ReadAllText("/proc/self/status");
            if (!Status.Contains("Uid:\t65532\t65532")) throw new Exception("wrong uid");
            if (!Status.Contains("NoNewPrivs:\t1")) throw new Exception("privilege escalation allowed");
            if (!Status.Contains("CapEff:\t0000000000000000")) throw new Exception("capabilities retained");
            if (File.ReadAllText("/sys/fs/cgroup/memory.max").Trim() != "536870912") throw new Exception("memory unbounded");
            if (File.ReadAllText("/sys/fs/cgroup/pids.max").Trim() != "128") throw new Exception("processes unbounded");
            try { File.WriteAllText("/root-write-test", "x"); throw new Exception("root writable"); }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            using var Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using var Deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await Socket.ConnectAsync("1.1.1.1", 443, Deadline.Token); throw new Exception("network reachable"); }
            catch (SocketException) { }
            catch (OperationCanceledException) { }
            Console.Write("isolated");
            """;
        var Result = await (await ExecutorAsync()).ExecuteAsync(Source, "", default);
        Result.Evidence.Status.Should().Be("Completed", Result.Evidence.StandardError);
        Result.Output.Should().Be("isolated");
    }

    [Fact]
    public async Task InfiniteOutputIsStoppedAndContainerIsRemoved()
    {
        var Result = await (await ExecutorAsync()).ExecuteAsync("while (true) Console.Write(new string('x', 4096));", "", default);
        Result.Evidence.Status.Should().Be("OutputLimitExceeded");
        Result.Output.Should().BeEmpty();
        await AssertNoContainersAsync();
    }

    [Fact]
    public async Task InfiniteProgramTimesOutAndContainerIsRemoved()
    {
        var Result = await (await ExecutorAsync()).ExecuteAsync("while (true) Thread.Sleep(1000);", "", default);
        Result.Evidence.Status.Should().Be("TimedOut");
        Result.Evidence.DurationSeconds.Should().BeInRange(85, 105);
        await AssertNoContainersAsync();
    }

    [Fact]
    public async Task HostCancellationRemovesSandbox()
    {
        using var Cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var Executor = await ExecutorAsync();
        await FluentActions.Awaiting(() => Executor.ExecuteAsync("while (true) Thread.Sleep(1000);", "", Cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        await AssertNoContainersAsync();
    }

    private static async Task AssertNoContainersAsync()
    {
        using var Process = System.Diagnostics.Process.Start(new ProcessStartInfo("docker", "ps -aq --filter label=personalagent.automation-sandbox=true")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var Output = await Process.StandardOutput.ReadToEndAsync();
        await Process.WaitForExitAsync();
        Output.Trim().Should().BeEmpty();
    }
}
