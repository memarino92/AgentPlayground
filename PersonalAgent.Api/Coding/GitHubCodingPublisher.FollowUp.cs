using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

using PersonalAgent.Contracts.Coding;
using PersonalAgent.Integrations;

namespace PersonalAgent.Api.Coding;

internal sealed partial class GitHubCodingPublisher
{
    public async Task<CodingRevisionTarget> PrepareRevisionAsync(CodingSettings Settings, CodingJob Parent, string FollowUp, CancellationToken Token)
    {
        var Match = Regex.Match(Parent.PullRequestUrl ?? "", $@"\Ahttps://github\.com/{Regex.Escape(Settings.Repository)}/pull/([1-9][0-9]*)\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!Match.Success || !int.TryParse(Match.Groups[1].Value, out var Number))
            throw new CodingRevisionConflict("The saved pull request URL is invalid.");
        var Credential = await CredentialAsync(Settings, Token);
        await VerifyProtectionAsync(Settings, Credential, Token);
        var Root = $"repos/{Settings.Repository}";
        var Pr = await ReadRevisionPrAsync(Settings, Parent.Branch, Parent.RootJobId ?? Parent.Id, Number, Credential, Token);
        var Head = Pr.GetProperty("head").GetProperty("sha").GetString()!;
        if (!Regex.IsMatch(Head, @"\A[a-f0-9]{40}\z")) throw new CodingRevisionConflict("The pull request head is invalid.");
        var Evidence = FollowUp == "review"
            ? await ReviewEvidenceAsync(Root, Number, Credential, Token)
            : await CheckEvidenceAsync(Root, Head, Token);
        return new(Number, Head, Evidence.Length > 12_000 ? Evidence[..12_000] : Evidence);
    }

    private async Task<string> ReviewEvidenceAsync(string Root, int Number, string Credential, CancellationToken Token)
    {
        var Reviews = await SendAsync(HttpMethod.Get, $"{Root}/pulls/{Number}/reviews?per_page=100", Credential, null, Token);
        var Comments = await SendAsync(HttpMethod.Get, $"{Root}/pulls/{Number}/comments?sort=updated&direction=desc&per_page=30", Credential, null, Token);
        var ReviewText = Reviews.EnumerateArray().Reverse().Where(Review => Field(Review, "state") is "CHANGES_REQUESTED" or "COMMENTED")
            .Take(10).Select(Review => $"Review ({Field(Review, "state")}): {Field(Review, "body", 1800)}");
        var InlineText = Comments.EnumerateArray().Select(Comment => $"Inline review on {Field(Comment, "path", 250)}: {Field(Comment, "body", 1200)}");
        return string.Join('\n', ReviewText.Concat(InlineText));
    }

    private async Task<string> CheckEvidenceAsync(string Root, string Head, CancellationToken Token)
    {
        // This runner only works with public repositories. Public check reads require no new App privileges.
        var Checks = await SendAsync(HttpMethod.Get, $"{Root}/commits/{Head}/check-runs?per_page=100", "", null, Token);
        var Evidence = new List<string>();
        foreach (var Check in Checks.GetProperty("check_runs").EnumerateArray()
            .Where(Check => Field(Check, "conclusion") is "failure" or "timed_out" or "action_required" or "startup_failure").Take(6))
        {
            Evidence.Add($"{Field(Check, "name")} ({Field(Check, "conclusion")}): {Field(Check, "details_url", 500)}");
            if (Check.TryGetProperty("output", out var Output) && Output.ValueKind == JsonValueKind.Object)
                Evidence.Add($"{Field(Output, "title", 300)}\n{Field(Output, "summary", 1000)}\n{Field(Output, "text", 1000)}");
            if (!Check.TryGetProperty("id", out var Id) || !Id.TryGetInt64(out var CheckId)) continue;
            var Annotations = await SendAsync(HttpMethod.Get, $"{Root}/check-runs/{CheckId}/annotations?per_page=20", "", null, Token);
            Evidence.AddRange(Annotations.EnumerateArray().Where(Annotation => Field(Annotation, "annotation_level") == "failure")
                .Select(Annotation => $"{Field(Annotation, "path", 250)}: {Field(Annotation, "message", 700)}"));
        }
        return Evidence.Count > 0 ? string.Join('\n', Evidence) : "GitHub exposed no failing check runs for this commit.";
    }

    private async Task<string> UpdateRevisionAsync(CodingJob Job, string Root, string Credential, string Sha, CancellationToken Token)
    {
        var Pr = await ReadRevisionPrAsync(Job.Settings, Job.Branch, Job.RootJobId!.Value, Job.PullRequestNumber!.Value, Credential, Token);
        var Head = Pr.GetProperty("head").GetProperty("sha").GetString();
        var Url = Pr.GetProperty("html_url").GetString()!;
        if (Head == Sha) return Url;
        if (Head != Job.BaseSha) throw new CodingRevisionConflict("The PR branch moved while this job was running. Start a new follow-up against its current head.");
        try
        {
            await SendAsync(HttpMethod.Patch, $"{Root}/git/refs/heads/{Job.Branch}", Credential, new { sha = Sha, force = false }, Token);
        }
        catch (HttpRequestException Error) when (Error.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
        {
            var Ref = await SendAsync(HttpMethod.Get, $"{Root}/git/ref/heads/{Job.Branch}", Credential, null, Token);
            var Current = Ref.GetProperty("object").GetProperty("sha").GetString();
            if (Current == Sha) return Url;
            if (Current != Job.BaseSha) throw new CodingRevisionConflict("The PR branch moved. Start a new follow-up against its current head.");
            throw new CodingRevisionConflict("GitHub refused the fast-forward update. Check branch rules before retrying.");
        }
        return Url;
    }

    private async Task<JsonElement> ReadRevisionPrAsync(CodingSettings Settings, string Branch, Guid RootJobId,
        int Number, string Credential, CancellationToken Token)
    {
        var Pr = await SendAsync(HttpMethod.Get, $"repos/{Settings.Repository}/pulls/{Number}", Credential, null, Token);
        var Head = Pr.GetProperty("head");
        if (Field(Pr, "state") != "open" || Field(Head, "ref") != Branch || Branch != $"feat/platform-improvement-{RootJobId:N}"
            || !Head.TryGetProperty("repo", out var Repository) || !string.Equals(Field(Repository, "full_name", 250), Settings.Repository, StringComparison.OrdinalIgnoreCase)
            || Field(Pr.GetProperty("base"), "ref") != Settings.BaseBranch
            || !(Pr.GetProperty("body").GetString() ?? "").Contains($"<!-- coding-job:{RootJobId:D} -->", StringComparison.Ordinal))
            throw new CodingRevisionConflict("The tracked pull request is closed or its branch, repository or ownership marker changed.");
        return Pr;
    }

    private static string Field(JsonElement Object, string Name, int Limit = 100) =>
        Object.ValueKind == JsonValueKind.Object && Object.TryGetProperty(Name, out var Value) && Value.ValueKind == JsonValueKind.String
            ? Value.GetString() is { } Text ? Text[..Math.Min(Text.Length, Limit)] : "" : "";
}
