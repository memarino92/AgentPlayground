using System.Diagnostics;

using PersonalAgent.Api.Coding;
using PersonalAgent.Contracts.Coding;
using PersonalAgent.Integrations;
using Xunit;

namespace PersonalAgent.Api.Tests.Services;

public sealed class CodingRebaseGitTests
{
    [Fact]
    public async Task ReplaysOriginalCommitsAndRejectsAConcurrentBranchUpdate()
    {
        using var Deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var Token = Deadline.Token;
        var Root = Directory.CreateTempSubdirectory("coding-rebase-test-");
        try
        {
            var Remote = Path.Combine(Root.FullName, "remote");
            Directory.CreateDirectory(Remote);
            await Git(Remote, ["init", "--bare"]);
            await Git(Root.FullName, ["init", "--initial-branch=main"]);
            await Git(Root.FullName, ["config", "user.name", "Fixture"]);
            await Git(Root.FullName, ["config", "user.email", "fixture@example.com"]);
            await File.WriteAllTextAsync(Path.Combine(Root.FullName, "change.txt"), "original\n", Token);
            await File.WriteAllTextAsync(Path.Combine(Root.FullName, "removed.txt"), "remove this\n", Token);
            await Save("feat: original");
            var JobId = Guid.NewGuid();
            var Branch = $"feat/platform-improvement-{JobId:N}";
            await Git(Root.FullName, ["checkout", "-b", Branch]);
            await File.WriteAllTextAsync(Path.Combine(Root.FullName, "change.txt"), "from PR\n", Token);
            var First = await Save("feat: tracked change");
            File.Delete(Path.Combine(Root.FullName, "removed.txt"));
            var Head = await Save("fix: remove unused file");
            await Git(Root.FullName, ["checkout", "main"]);
            await File.WriteAllTextAsync(Path.Combine(Root.FullName, "change.txt"), "from main\n", Token);
            var Base = await Save("feat: advance main");
            await Git(Root.FullName, ["push", Remote, "main", Branch]);

            using var Publisher = new CodingRebaseGit();
            await Publisher.InitializeAsync(Remote, Base, Head, Token);
            var Change = new CodingFile("change.txt", "resolved\n");
            var Deletion = new CodingFile("removed.txt", null);
            var Job = new CodingJob
            {
                BaseSha = Base, ExpectedBranchHead = Head, Branch = Branch,
                CreatedAt = new(2026, 11, 22, 0, 0, 0, TimeSpan.Zero),
                Artifact = new(Base, [Change, Deletion], [], "resolved") { RebaseCommits = [new(First, [Change]), new(Head, [Deletion])] }
            };
            var Sha = await Publisher.ReplayAsync(Job, Token);
            Assert.Equal(Sha, await Publisher.ReplayAsync(Job, Token));

            // Simulate an owner pushing a new commit after the rebase was validated.
            await Git(Root.FullName, ["checkout", Branch]);
            await File.WriteAllTextAsync(Path.Combine(Root.FullName, "preserve.txt"), "new owner work\n", Token);
            var Moved = await Save("fix: owner update");
            await Git(Root.FullName, ["push", Remote, Branch]);
            Assert.Equal(CodingRebasePush.Rejected, await Publisher.PushAsync(Remote, Branch, Head, Sha, null, Token));
            Assert.Equal(Moved, await Git(Remote, ["rev-parse", Branch]));

            // Recreate the original scenario in this disposable remote only.
            await Git(Remote, ["update-ref", $"refs/heads/{Branch}", Head, Moved]);
            Assert.Equal(CodingRebasePush.Updated, await Publisher.PushAsync(Remote, Branch, Head, Sha, null, Token));
            Assert.Equal(Base, await Git(Remote, ["rev-parse", Sha + "^^"]));
            Assert.Equal("feat: tracked change", await Git(Remote, ["show", "-s", "--format=%s", Sha + "^"]));
            Assert.Equal("fix: remove unused file", await Git(Remote, ["show", "-s", "--format=%s", Sha]));
            Assert.Equal("resolved", await Git(Remote, ["show", $"{Sha}:change.txt"]));
            Assert.Empty(await Git(Remote, ["ls-tree", Sha, "--", "removed.txt"]));

            async Task<string> Save(string Message)
            {
                await Git(Root.FullName, ["add", "-A"]);
                await Git(Root.FullName, ["-c", "commit.gpgSign=false", "commit", "-m", Message]);
                return await Git(Root.FullName, ["rev-parse", "HEAD"]);
            }

            async Task<string> Git(string Path, string[] Arguments)
            {
                using var Proc = new Process { StartInfo = new("git") { WorkingDirectory = Path, RedirectStandardOutput = true, RedirectStandardError = true } };
                foreach (var Argument in Arguments) Proc.StartInfo.ArgumentList.Add(Argument);
                Proc.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
                Proc.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = System.IO.Path.Combine(Root.FullName, "empty-config");
                Proc.Start();
                var Output = Proc.StandardOutput.ReadToEndAsync(Token);
                var Errors = Proc.StandardError.ReadToEndAsync(Token);
                await Proc.WaitForExitAsync(Token);
                Assert.True(Proc.ExitCode == 0, await Errors);
                return (await Output).Trim();
            }
        }
        finally { CodingRebaseGit.RemoveTemporaryRepository(Root); }
    }
}
