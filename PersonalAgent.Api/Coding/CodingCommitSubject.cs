using System.Text.RegularExpressions;

using PersonalAgent.Integrations;

namespace PersonalAgent.Api.Coding;

// Publication metadata only: never interpolate candidate text into a shell or Git command.
internal static class CodingCommitSubject
{
    internal static string Create(CodingJob Job)
    {
        var Prefix = Job.RootJobId is null ? "feat" : "fix";
        foreach (var Source in new[] { Job.Artifact?.Summary, Job.Instruction })
        {
            var Line = Source?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim().Trim('`', '*', '#', ' ');
            if (string.IsNullOrWhiteSpace(Line)) continue;
            var Conventional = Regex.Match(Line, @"\A(feat|fix|docs|refactor|test|chore|perf|build|ci|style|revert)(\([a-zA-Z0-9._/-]+\))?(!)?:\s+(.+)\z");
            var Description = Conventional.Success ? Conventional.Groups[4].Value : Line;
            // Reject identifiers, credential-like tokens and command/instruction syntax rather than
            // truncating them into apparently useful descriptions. Existing summaries remain public PR data.
            if (Description.Length > 90 || !Regex.IsMatch(Description, @"\A[A-Za-z][A-Za-z0-9 ,.'()/-]*\z")
                || Regex.IsMatch(Description, @"[a-fA-F0-9]{16,}|\S{32,}|\b(sk-|gh[pousr]_|github_pat_|Bearer\s|password|secret|token)|\b(platform improvement|requested platform change|recorded checks|review the diff)\b", RegexOptions.IgnoreCase)) continue;
            var Subject = Conventional.Success ? Line : $"{Prefix}: {Description.TrimEnd('.')}";
            if (Subject.Length <= 100) return Subject;
        }

        // Older images or verbose summaries may supply no usable subject. File names are more
        // informative than a job identifier, and are reduced to a small metadata-safe alphabet.
        var File = Job.Artifact?.Files.Select(F => Path.GetFileName(F.Path))
            .Order(StringComparer.Ordinal).FirstOrDefault();
        var Name = File is null ? "" : Regex.Replace(File, @"[^A-Za-z0-9._-]", "");
        return Name.Length is > 0 and <= 60
            ? $"{Prefix}: update {Name}" : $"{Prefix}: update application behavior and validation";
    }
}
