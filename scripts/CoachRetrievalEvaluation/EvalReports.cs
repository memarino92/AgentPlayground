using System.Globalization;
using System.Text;
using System.Xml.Linq;

internal static class EvalReports
{
    internal static async Task WriteAsync(string Root, string Name, List<EvalResult> Results)
    {
        var Suite = new XElement("testsuite", new XAttribute("name", Name), new XAttribute("tests", Results.Count), new XAttribute("failures", Results.Count(r => !r.Passed)),
            Results.Select(r => new XElement("testcase", new XAttribute("classname", r.Model), new XAttribute("name", $"{r.CaseId}-{r.Repeat}"),
                new XAttribute("time", (r.ElapsedMs / 1000.0).ToString(CultureInfo.InvariantCulture)),
                r.Passed ? null : new XElement("failure", string.Join(", ", r.Checks.Where(c => !c.Value).Select(c => c.Key))))));
        await File.WriteAllTextAsync(Path.Combine(Root, "junit.xml"), new XDocument(Suite).ToString());
        var Report = new StringBuilder("# Coaching model evaluation\n\nDeterministic checks on a fixed private tuning corpus; human review is still required for semantic correctness.\n\n| Model | Passes | Median ms | Input tokens | Output tokens |\n|---|---:|---:|---:|---:|\n");
        foreach (var Group in Results.GroupBy(r => r.Model))
        {
            var Ordered = Group.Where(r => r.ProviderFailure is null).Select(r => r.ElapsedMs).Order().ToArray();
            var Median = Ordered.Length == 0 ? "unavailable" : ((Ordered[(Ordered.Length - 1) / 2] + Ordered[Ordered.Length / 2]) / 2.0).ToString("0.0", CultureInfo.InvariantCulture);
            Report.AppendLine($"| {Group.Key} | {Group.Count(r => r.Passed)}/{Group.Count()} | {Median} | {Tokens(Group.Select(r => r.InputTokens))} | {Tokens(Group.Select(r => r.OutputTokens))} |");
        }
        Report.AppendLine("\nLatency excludes provider-failed trials. Chat usage is provider-reported; embedding usage and dollar cost are not measured. Unavailable usage remains null in individual results and is not represented as zero. No production model policy was modified.");
        await File.WriteAllTextAsync(Path.Combine(Root, "report.md"), Report.ToString());
    }

    private static string Tokens(IEnumerable<long?> Values)
    {
        var Items = Values.ToArray();
        if (Items.All(v => v is null)) return "unavailable";
        return Items.Where(v => v.HasValue).Sum(v => v!.Value).ToString(CultureInfo.InvariantCulture) + (Items.Any(v => v is null) ? " (partial)" : string.Empty);
    }
}
