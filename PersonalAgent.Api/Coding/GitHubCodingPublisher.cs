using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using PersonalAgent.Contracts.Coding;
using PersonalAgent.Integrations;

namespace PersonalAgent.Api.Coding;

internal interface ICodingPublisher
{
    Task<string> PrepareAsync(CodingSettings Settings, CancellationToken Token);
    Task<string> PublishAsync(CodingJob Job, CancellationToken Token);
}

// Git data APIs construct a tree without checking out or executing candidate code in the publisher.
internal sealed class GitHubCodingPublisher(HttpClient Http, CodingJobStore Store) : ICodingPublisher
{
    public async Task<string> PrepareAsync(CodingSettings Settings, CancellationToken Token)
    {
        var Credential = await CredentialAsync(Settings, Token);
        await VerifyProtectionAsync(Settings, Credential, Token);
        var Repo = await SendAsync(HttpMethod.Get, $"repos/{Settings.Repository}", Credential, null, Token);
        if (Repo.GetProperty("private").GetBoolean()) throw new InvalidOperationException("This coding profile supports public repositories only.");
        var Ref = await SendAsync(HttpMethod.Get, $"repos/{Settings.Repository}/git/ref/heads/{Settings.BaseBranch}", Credential, null, Token);
        return Ref.GetProperty("object").GetProperty("sha").GetString()!;
    }

    public async Task<string> PublishAsync(CodingJob Job, CancellationToken Token)
    {
        ValidateArtifact(Job);
        var Credential = await CredentialAsync(Job.Settings, Token);
        await VerifyProtectionAsync(Job.Settings, Credential, Token);
        var Root = $"repos/{Job.Settings.Repository}";
        var Existing = await SendAsync(HttpMethod.Get,
            $"{Root}/pulls?state=all&head={Uri.EscapeDataString(Job.Settings.Repository.Split('/')[0] + ":" + Job.Branch)}", Credential, null, Token);
        if (Existing.GetArrayLength() > 0)
        {
            var Pr = Existing[0];
            if (!(Pr.GetProperty("body").GetString() ?? "").Contains(Marker(Job), StringComparison.Ordinal)
                || Pr.GetProperty("base").GetProperty("ref").GetString() != Job.Settings.BaseBranch)
                throw new InvalidOperationException("Existing PR does not match this job.");
            return Pr.GetProperty("html_url").GetString()!;
        }
        var Commit = await SendAsync(HttpMethod.Get, $"{Root}/git/commits/{Job.BaseSha}", Credential, null, Token);
        var Tree = await SendAsync(HttpMethod.Post, $"{Root}/git/trees", Credential, new
        {
            base_tree = Commit.GetProperty("tree").GetProperty("sha").GetString(),
            tree = Job.Artifact!.Files.Select(F => F.Content is null
                ? (object)new { path = F.Path, mode = F.Mode, type = "blob", sha = (string?)null }
                : new { path = F.Path, mode = F.Mode, type = "blob", content = F.Content }).ToArray()
        }, Token);
        // Stable metadata produces a stable commit SHA on publication retries.
        var NewCommit = await SendAsync(HttpMethod.Post, $"{Root}/git/commits", Credential, new
        {
            message = $"feat: implement platform improvement {Job.Id:N}",
            tree = Tree.GetProperty("sha").GetString(), parents = new[] { Job.BaseSha },
            author = new { name = "Platform improvements", email = "platform-improvements@users.noreply.github.com", date = Job.CreatedAt },
            committer = new { name = "Platform improvements", email = "platform-improvements@users.noreply.github.com", date = Job.CreatedAt }
        }, Token);
        var Sha = NewCommit.GetProperty("sha").GetString();
        try { await SendAsync(HttpMethod.Post, $"{Root}/git/refs", Credential, new { @ref = "refs/heads/" + Job.Branch, sha = Sha }, Token); }
        catch (HttpRequestException E) when (E.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var Ref = await SendAsync(HttpMethod.Get, $"{Root}/git/ref/heads/{Job.Branch}", Credential, null, Token);
            if (Ref.GetProperty("object").GetProperty("sha").GetString() != Sha)
                throw new InvalidOperationException("Publication branch contains a different commit; it will not be overwritten.");
        }
        var Body = $"{Marker(Job)}\n\n{Job.Artifact.Summary}\n\nRequested change:\n{Job.Instruction}\n\nBase: `{Job.BaseSha}`\n\nValidation:\n"
            + string.Join("\n", Job.Artifact.Checks.Select(C => $"- `{C.Command}`: exit {C.ExitCode}"))
            + "\n\nGenerated in an isolated coding environment. Review and merge remain with the maintainer. Full diagnostics are in the private coding-job dashboard.";
        var Created = await SendAsync(HttpMethod.Post, $"{Root}/pulls", Credential,
            new { title = "Platform improvement: " + Job.Instruction.Split('\n')[0][..Math.Min(Job.Instruction.Split('\n')[0].Length, 120)],
                head = Job.Branch, @base = Job.Settings.BaseBranch, body = Body, draft = true }, Token);
        return Created.GetProperty("html_url").GetString()!;
    }

    internal static void ValidateArtifact(CodingJob Job)
    {
        var A = Job.Artifact;
        if (A is null || A.BaseSha != Job.BaseSha || !Regex.IsMatch(Job.BaseSha, "\\A[a-f0-9]{40}\\z")
            || Job.Branch != $"feat/platform-improvement-{Job.Id:N}" || A.Files is not { Length: > 0 and <= 50 }
            || A.Checks is not { Length: > 0 and <= 4 } || A.Checks.Any(C => C is null || C.ExitCode != 0)
            || string.IsNullOrWhiteSpace(A.Summary) || A.Summary.Length > 4000
            || Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(A)) > CodingJobs.MaxArtifactBytes)
            throw new ArgumentException("A bounded change with matching base and passing validation is required.");
        var Paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var F in A.Files)
            if (F is null || F.Path is null || !Regex.IsMatch(F.Path, @"\A[A-Za-z0-9_./-]{1,240}\z") || F.Path.StartsWith('/')
                || F.Path.Split('/').Any(P => P is "" or "." or ".." || P.StartsWith(".git", StringComparison.OrdinalIgnoreCase))
                || F.Path.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) || F.Path.EndsWith(".key", StringComparison.OrdinalIgnoreCase)
                || F.Path.Split('/').Any(P => P is ".env" or "node_modules" or "bin" or "obj")
                || F.Mode is not ("100644" or "100755") || !Paths.Add(F.Path))
                throw new ArgumentException("Artifact contains a forbidden, duplicate or unsupported path/mode.");
    }

    private static string Marker(CodingJob Job) => $"<!-- coding-job:{Job.Id:D} -->";
    private async Task VerifyProtectionAsync(CodingSettings S, string Credential, CancellationToken Token)
    {
        JsonElement P;
        try { P = await SendAsync(HttpMethod.Get, $"repos/{S.Repository}/branches/{S.BaseBranch}/protection", Credential, null, Token); }
        catch (HttpRequestException E) when (E.StatusCode == HttpStatusCode.NotFound)
        {
            await VerifyRulesetAsync(S, Credential, Token);
            return;
        }
        if (!P.TryGetProperty("enforce_admins", out var Admins) || !Admins.GetProperty("enabled").GetBoolean()
            || !P.TryGetProperty("required_pull_request_reviews", out var Reviews)
            || Reviews.GetProperty("required_approving_review_count").GetInt32() < 1)
            throw new InvalidOperationException("Main must require review and enforce protection for administrators.");
        if (Reviews.TryGetProperty("bypass_pull_request_allowances", out var Bypass)
            && Bypass.TryGetProperty("apps", out var Apps) && Apps.EnumerateArray().Any(A => A.GetProperty("id").GetInt64() == S.GitHubAppId))
            throw new InvalidOperationException("The publisher App must not bypass required review.");
    }

    private async Task VerifyRulesetAsync(CodingSettings S, string Credential, CancellationToken Token)
    {
        var Rules = await SendAsync(HttpMethod.Get, $"repos/{S.Repository}/rules/branches/{S.BaseBranch}?per_page=100", Credential, null, Token);
        foreach (var Rule in Rules.EnumerateArray())
        {
            if (Rule.GetProperty("type").GetString() != "pull_request"
                || Rule.GetProperty("parameters").GetProperty("required_approving_review_count").GetInt32() < 1
                || Rule.GetProperty("ruleset_source_type").GetString() != "Repository"
                || Rule.GetProperty("ruleset_source").GetString() != S.Repository) continue;
            var Id = Rule.GetProperty("ruleset_id").GetInt64();
            var Set = await SendAsync(HttpMethod.Get, $"repos/{S.Repository}/rulesets/{Id}", Credential, null, Token);
            if (Set.GetProperty("enforcement").GetString() != "active") continue;
            // GitHub omits bypass actors for Administration:read. A maintainer can attest to a
            // specific ruleset revision without granting this publisher permission to edit it.
            if (Set.TryGetProperty("bypass_actors", out var Bypass))
            {
                if (Bypass.EnumerateArray().All(A => A.GetProperty("actor_type").GetString() == "User")) return;
                continue;
            }
            if (S.VerifiedRulesetId == Id && CodingJobs.RulesetTimestamp(S.VerifiedRulesetUpdatedAt) is { } Verified
                && Set.TryGetProperty("updated_at", out var Updated)
                && CodingJobs.RulesetTimestamp(Updated.GetString()) == Verified) return;
        }
        throw new InvalidOperationException("Main needs an active repository review ruleset with at least one approval and no role or App bypass, or enforced classic protection. If GitHub hides bypass actors, a deployment administrator must review the bypass list and save the current ruleset ID and updated_at in Platform coding settings.");
    }

    private async Task<string> CredentialAsync(CodingSettings Settings, CancellationToken Token)
    {
        var Current = await Store.SettingsAsync(Token);
        if (!Current.View.Settings.Enabled || Current.View.Settings.Repository != Settings.Repository
            || Current.View.Settings.GitHubAppId != Settings.GitHubAppId || Current.View.Settings.InstallationId != Settings.InstallationId
            || Current.View.Settings.VerifiedRulesetId != Settings.VerifiedRulesetId
            || Current.View.Settings.VerifiedRulesetUpdatedAt != Settings.VerifiedRulesetUpdatedAt)
            throw new UnauthorizedAccessException("Coding publication configuration changed or is disabled.");
        var Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        static string B64(byte[] B) => Convert.ToBase64String(B).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var Unsigned = B64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}")) + "."
            + B64(JsonSerializer.SerializeToUtf8Bytes(new { iat = Now - 60, exp = Now + 540, iss = Settings.GitHubAppId.ToString() }));
        using var Rsa = RSA.Create(); Rsa.ImportFromPem(Current.Secrets.GitHubPrivateKey);
        var Jwt = Unsigned + "." + B64(Rsa.SignData(Encoding.UTF8.GetBytes(Unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var Result = await SendAsync(HttpMethod.Post, $"app/installations/{Settings.InstallationId}/access_tokens", Jwt,
            new { repositories = new[] { Settings.Repository.Split('/')[1] }, permissions = new { contents = "write", pull_requests = "write", administration = "read" } }, Token);
        return Result.GetProperty("token").GetString()!;
    }

    private async Task<JsonElement> SendAsync(HttpMethod Method, string Path, string Credential, object? Body, CancellationToken Token)
    {
        using var Request = new HttpRequestMessage(Method, "https://api.github.com/" + Path);
        Request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Credential);
        Request.Headers.UserAgent.ParseAdd("AgentPlayground-Coding/1.0");
        Request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (Body is not null) Request.Content = JsonContent.Create(Body);
        using var Response = await Http.SendAsync(Request, Token);
        Response.EnsureSuccessStatusCode();
        return await Response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }
}
