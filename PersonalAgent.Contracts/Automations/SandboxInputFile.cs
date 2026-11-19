using System.Text.RegularExpressions;

namespace PersonalAgent.Contracts.Automations;

public sealed record SandboxInputFile(string Name, string Reference)
{
    public const int MaxBytes = 16 * 1024 * 1024;

    public static void Validate(IReadOnlyList<SandboxInputFile> Files)
    {
        if (Files.Count > 16 || Files.Any(F => F is null || !ValidName(F.Name) || !ValidReference(F.Reference))
            || Files.Select(F => F.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Files.Count)
            throw new ArgumentException("Supply up to 16 files with unique portable names and attachment:UUID or artifact:RUN_UUID:STEP:FILE references.");
    }

    public static bool ValidName(string? Name) => Name is not null && Regex.IsMatch(Name, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,95}\z");

    private static bool ValidReference(string? Reference)
    {
        var Parts = Reference?.Split(':');
        return Parts is ["attachment", var Id] && Guid.TryParseExact(Id, "D", out _)
            || Parts is ["artifact", var Run, var Step, var File] && Guid.TryParseExact(Run, "D", out _)
                && int.TryParse(Step, out var S) && S >= 0 && int.TryParse(File, out var F) && F >= 0;
    }
}
