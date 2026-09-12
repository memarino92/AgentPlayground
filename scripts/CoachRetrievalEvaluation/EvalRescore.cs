using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.AI;

internal static class EvalRescore
{
    internal static async Task RunAsync(string DirectoryPath, string CasePath)
    {
        var DatasetBytes = await File.ReadAllBytesAsync(CasePath);
        var Dataset = JsonSerializer.Deserialize<EvalDataset>(DatasetBytes)!;
        var Root = Path.Combine(DirectoryPath, $"rubric-v{Dataset.Version}-{DateTime.UtcNow:yyyyMMddTHHmmssZ}");
        Directory.CreateDirectory(Root);
        var Results = new List<EvalResult>();
        var Options = new JsonSerializerOptions { WriteIndented = true };
        foreach (var FilePath in Directory.GetFiles(DirectoryPath, "*.json"))
        {
            using var Doc = JsonDocument.Parse(await File.ReadAllTextAsync(FilePath));
            if (Doc.RootElement.ValueKind != JsonValueKind.Object || !Doc.RootElement.TryGetProperty("result", out var RawResult)) continue;
            var Original = RawResult.Deserialize<EvalResult>()!;
            var Test = Dataset.Cases.Single(c => c.Id == Original.CaseId);
            var Answer = Doc.RootElement.GetProperty("answer").GetString();
            var Trace = new TrialTrace();
            foreach (var Event in Doc.RootElement.GetProperty("Events").EnumerateArray())
            {
                Trace.Evidence.AddRange(Event.GetProperty("evidence").EnumerateArray().Select(e => e.GetString()!));
                foreach (var Call in Event.GetProperty("calls").EnumerateArray())
                    Trace.Calls.Add(new FunctionCallContent("saved-call", Call.GetProperty("Name").GetString()!,
                        Call.GetProperty("Arguments").EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone())));
            }
            var Checks = EvalScorer.Score(Test, Answer, Trace);
            Checks["providerSucceeded"] = Original.ProviderFailure is null;
            Results.Add(Original with { Passed = Checks.Values.All(v => v), Checks = Checks });
        }
        if (Results.Count == 0) throw new InvalidOperationException("No saved evaluation trials were found; an empty run cannot pass.");
        await File.WriteAllTextAsync(Path.Combine(Root, "results.json"), JsonSerializer.Serialize(Results, Options));
        await File.WriteAllBytesAsync(Path.Combine(Root, "cases.json"), DatasetBytes);
        await File.WriteAllTextAsync(Path.Combine(Root, "manifest.json"), JsonSerializer.Serialize(new {
            protocol = $"coach-retrieval-v{Dataset.Version}", sourceRun = Path.GetFullPath(DirectoryPath),
            datasetSha256 = Convert.ToHexString(SHA256.HashData(DatasetBytes)),
            scorerSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync("scripts/CoachRetrievalEvaluation/EvalScorer.cs"))),
            reason = "Correct valid bare/whitespace citation parsing and false-positive negated attribution checks; rescore all saved outputs without new model calls.",
            implementationHashes = new[] { "EvalScorer.cs", "EvalRescore.cs", "EvalReports.cs" }
                .ToDictionary(n => n, n => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine("scripts/CoachRetrievalEvaluation", n))))),
            originalResultsPreserved = true
        }, Options));
        await EvalReports.WriteAsync(Root, Dataset.Name, Results);
        Console.WriteLine($"Rescored evidence: {Root}");
        Environment.ExitCode = Results.All(r => r.Passed) ? 0 : 1;
    }
}
