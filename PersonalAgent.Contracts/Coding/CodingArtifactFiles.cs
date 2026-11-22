using System.Text.RegularExpressions;

namespace PersonalAgent.Contracts.Coding;

public static partial class CodingArtifactFiles
{
    public static bool AreAllowed(CodingFile[]? Files)
    {
        if (Files is not { Length: <= 50 }) return false;
        var Paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var File in Files)
        {
            if (File?.Path is not { } Path || !ArtifactPath().IsMatch(Path) || Path.StartsWith('/')
                || string.Equals(Path, ".agentplayground-coding-progress.md", StringComparison.OrdinalIgnoreCase)
                || Path.Split('/').Any(Part => Part is "" or "." or ".." or ".env" or "node_modules" or "bin" or "obj"
                    || Part.StartsWith(".git", StringComparison.OrdinalIgnoreCase))
                || Path.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) || Path.EndsWith(".key", StringComparison.OrdinalIgnoreCase)
                || File.Mode is not ("100644" or "100755") || !Paths.Add(Path))
                return false;
        }
        return true;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_./-]{1,240}\z")]
    private static partial Regex ArtifactPath();
}
