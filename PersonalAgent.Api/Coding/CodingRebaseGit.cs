using System.ComponentModel;
using System.Diagnostics;
using System.Text;

using PersonalAgent.Contracts.Coding;
using PersonalAgent.Integrations;

namespace PersonalAgent.Api.Coding;

internal sealed partial class GitHubCodingPublisher
{
    private async Task<string> PublishRebaseAsync(CodingJob Job, string Credential, CancellationToken Token)
    {
        var Expected = Job.ExpectedBranchHead!;
        var Pr = await ReadRevisionPrAsync(Job.Settings, Job.Branch, Job.RootJobId!.Value, Job.PullRequestNumber!.Value, Credential, Token);
        var Url = Pr.GetProperty("html_url").GetString()!;
        var Head = Field(Pr.GetProperty("head"), "sha");
        var Saved = await Store.GetRebaseCandidateAsync(Job.Id, Token);
        if (Saved is not null && Head == Saved) return Url;
        if (Head != Expected)
            throw new CodingRevisionConflict("The PR branch moved while the rebase was running. Start a new rebase against its current head.");
        var Remote = $"https://github.com/{Job.Settings.Repository}.git";

        using var Git = new CodingRebaseGit();
        await Git.InitializeAsync(Remote, Job.BaseSha, Expected, Token);
        var Sha = await Git.ReplayAsync(Job, Token);
        if (await ReadBaseShaAsync(Job.Settings, Credential, Token) != Job.BaseSha)
            throw new CodingRevisionConflict("The base branch moved while the rebase was running. Start a new rebase against its current head.");
        if (!await Store.SaveRebaseCandidateAsync(Job.Id, Sha, Token))
            throw new CodingRevisionConflict("Could not record the same verified rebase candidate before publication.");

        // GitHub's REST force-update has no expected-head precondition.
        var Authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{Credential}"));
        var Outcome = await Git.PushAsync(Remote, Job.Branch, Expected, Sha, Authorization, Token);
        if (Outcome == CodingRebasePush.Updated) return Url;

        var Current = await ReadRevisionPrAsync(Job.Settings, Job.Branch, Job.RootJobId.Value, Job.PullRequestNumber.Value, Credential, Token);
        var CurrentHead = Field(Current.GetProperty("head"), "sha");
        if (CurrentHead == Sha) return Url;
        if (CurrentHead != Expected)
            throw new CodingRevisionConflict("The PR branch moved while the rebase was publishing. Its new commits were preserved.");
        if (Outcome == CodingRebasePush.Rejected)
            throw new CodingRevisionConflict("GitHub rejected the leased rebase update. Check the tracked branch rules.");
        throw new HttpRequestException("Could not confirm the leased rebase update.");
    }
}

internal enum CodingRebasePush { Updated, Rejected, Unconfirmed }

// Runs only trusted Git commands in a new bare repository. Candidate files arrive as bounded blobs.
internal sealed class CodingRebaseGit : IDisposable
{
    private readonly DirectoryInfo Directory = System.IO.Directory.CreateTempSubdirectory("coding-rebase-");

    public async Task InitializeAsync(string Remote, string Base, string Head, CancellationToken Token)
    {
        await GitAsync(["init", "--bare", "--template="], Token);
        await GitAsync(["fetch", "--no-tags", "--no-recurse-submodules", "--", Remote, Base, Head], Token);
    }

    public async Task<string> ReplayAsync(CodingJob Job, CancellationToken Token)
    {
        var Head = Job.ExpectedBranchHead!;
        var Base = Job.BaseSha;
        var Fork = (await GitAsync(["merge-base", Base, Head], Token)).Trim();
        var Original = (await GitAsync(["rev-list", "--reverse", "--max-count=26", $"{Fork}..{Head}"], Token))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var Commits = Job.Artifact!.RebaseCommits!;
        if (!Original.SequenceEqual(Commits.Select(C => C.OriginalSha)))
            throw new CodingRevisionConflict("The replayed commit sequence does not match the tracked PR history.");

        var Parent = Base;
        var Previous = Fork;
        foreach (var Commit in Commits)
        {
            var Metadata = (await GitAsync(["show", "-s", "--format=%P%x00%an%x00%ae%x00%aI%x00%B", Commit.OriginalSha], Token)).Split('\0', 5);
            if (Metadata.Length != 5 || Metadata[0] != Previous)
                throw new CodingRevisionConflict("The tracked PR history must be linear.");
            var Tree = await TreeAsync(Parent, Commit.Files, Token);
            var Identity = new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = Metadata[1], ["GIT_AUTHOR_EMAIL"] = Metadata[2], ["GIT_AUTHOR_DATE"] = Metadata[3],
                ["GIT_COMMITTER_NAME"] = "Platform improvements", ["GIT_COMMITTER_EMAIL"] = "platform-improvements@users.noreply.github.com",
                ["GIT_COMMITTER_DATE"] = Job.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            };
            Parent = (await GitAsync(["-c", "commit.gpgSign=false", "commit-tree", Tree, "-p", Parent, "-F", "-"],
                Token, Metadata[4].TrimEnd('\n') + "\n", Identity)).Trim();
            Previous = Commit.OriginalSha;
        }

        var ExpectedTree = await TreeAsync(Base, Job.Artifact.Files, Token);
        var ActualTree = (await GitAsync(["rev-parse", $"{Parent}^{{tree}}"], Token)).Trim();
        if (ExpectedTree != ActualTree)
            throw new CodingRevisionConflict("The rebased commit series does not match the tested file changes.");
        return Parent;
    }

    public async Task<CodingRebasePush> PushAsync(string Remote, string Branch, string Expected, string Sha, string? Authorization, CancellationToken Token)
    {
        var Ref = $"refs/heads/{Branch}";
        Dictionary<string, string>? Environment = Authorization is null ? null : new()
        {
            ["GIT_CONFIG_COUNT"] = "2", ["GIT_CONFIG_KEY_1"] = "http.https://github.com/.extraHeader",
            ["GIT_CONFIG_VALUE_1"] = $"Authorization: Basic {Authorization}"
        };
        var Result = await RunAsync(["push", "--porcelain", "--no-verify", $"--force-with-lease={Ref}:{Expected}", "--", Remote, $"{Sha}:{Ref}"],
            Token, null, Environment);
        if (Result.ExitCode == 0) return CodingRebasePush.Updated;
        return Result.Output.Split('\n').Any(Line => Line.StartsWith("!\t", StringComparison.Ordinal)
            && (Line.Contains("\t[rejected]", StringComparison.Ordinal) || Line.Contains("\t[remote rejected]", StringComparison.Ordinal)))
            ? CodingRebasePush.Rejected : CodingRebasePush.Unconfirmed;
    }

    private async Task<string> TreeAsync(string Parent, CodingFile[] Files, CancellationToken Token)
    {
        await GitAsync(["read-tree", Parent], Token);
        var Entries = new StringBuilder();
        foreach (var File in Files.Where(F => F.Content is null))
            Entries.Append("0 ").Append('0', 40).Append('\t').Append(File.Path).Append('\0');
        foreach (var File in Files.Where(F => F.Content is not null))
        {
            var Blob = (await GitAsync(["hash-object", "-w", "--stdin"], Token, File.Content!)).Trim();
            Entries.Append(File.Mode).Append(' ').Append(Blob).Append('\t').Append(File.Path).Append('\0');
        }
        if (Entries.Length > 0) await GitAsync(["update-index", "-z", "--index-info"], Token, Entries.ToString());
        return (await GitAsync(["write-tree"], Token)).Trim();
    }

    private async Task<string> GitAsync(string[] Arguments, CancellationToken Token, string? Input = null,
        Dictionary<string, string>? Extra = null)
    {
        var Result = await RunAsync(Arguments, Token, Input, Extra);
        if (Result.ExitCode != 0)
            throw new CodingRevisionConflict($"Git could not prepare the tracked rebase ({Arguments[0]}).");
        return Result.Output;
    }

    private async Task<(int ExitCode, string Output)> RunAsync(string[] Arguments, CancellationToken Token, string? Input,
        Dictionary<string, string>? Extra)
    {
        using var Process = new Process
        {
            StartInfo = new("git")
            {
                WorkingDirectory = Directory.FullName, UseShellExecute = false, RedirectStandardInput = Input is not null,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = Input is null ? null : new UTF8Encoding(false)
            }
        };
        foreach (var Argument in Arguments) Process.StartInfo.ArgumentList.Add(Argument);
        foreach (var Key in Process.StartInfo.Environment.Keys.Where(Key => Key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            Process.StartInfo.Environment.Remove(Key);
        var Environment = Process.StartInfo.Environment;
        Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(Directory.FullName, "empty-config");
        Environment["GIT_TERMINAL_PROMPT"] = "0";
        Environment["GIT_CONFIG_COUNT"] = "1";
        Environment["GIT_CONFIG_KEY_0"] = "http.followRedirects";
        Environment["GIT_CONFIG_VALUE_0"] = "false";
        if (Extra is not null)
            foreach (var (Key, Value) in Extra) Environment[Key] = Value;

        using var Deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var Stop = Deadline.Token;
        try { Process.Start(); }
        catch (Win32Exception) { throw new CodingRevisionConflict("Git is unavailable in the API publisher."); }
        var Output = Process.StandardOutput.ReadToEndAsync(Stop);
        var Errors = Process.StandardError.ReadToEndAsync(Stop);
        try
        {
            if (Input is not null)
            {
                await Process.StandardInput.WriteAsync(Input.AsMemory(), Stop);
                Process.StandardInput.Close();
            }
            await Process.WaitForExitAsync(Stop);
            await Errors;
            return (Process.ExitCode, await Output);
        }
        finally
        {
            if (!Process.HasExited) Process.Kill(entireProcessTree: true);
            try { await Task.WhenAll(Output, Errors); }
            catch (OperationCanceledException) { }
        }
    }

    public void Dispose() => RemoveTemporaryRepository(Directory);

    internal static void RemoveTemporaryRepository(DirectoryInfo Target)
    {
        // Git object files are read-only on Windows. Do not follow links during cleanup.
        if (OperatingSystem.IsWindows())
            foreach (var File in Target.EnumerateFiles("*", new EnumerationOptions
            {
                RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint
            }))
                if (File.IsReadOnly) File.IsReadOnly = false;
        Target.Delete(recursive: true);
    }
}
