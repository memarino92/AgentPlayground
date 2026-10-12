using System.Net;

using Microsoft.AspNetCore.Components;

using PersonalAgent.Contracts.Coding;
using PersonalAgent.Web.Services;

namespace PersonalAgent.Web.Components.Pages;

public partial class CodingSettingsPage : IAsyncDisposable
{
    [Inject] private PersonalAgentClient Client { get; set; } = default!;
    private readonly CancellationTokenSource Lifetime = new();
    private FormModel Model = new();
    private CodingModelOption[] Models = [];
    private bool Subscription => CodingJobs.IsSubscriptionModel(Model.ModelId);
    private long Revision;
    private bool Busy, Loaded, HasOpenRouterKey, HasGitHubKey;
    private string? Error, Notice;
    protected override Task OnInitializedAsync() => LoadAsync();
    private Task LoadAsync() => RunAsync(async () =>
    {
        Apply(await Client.GetCodingSettingsAsync(Lifetime.Token));
        Models = await Client.GetCodingModelsAsync(Lifetime.Token);
    });
    private void SelectModel()
    {
        if (Subscription && Model.Requests <= 12) Model.Requests = 250;
        if (!Subscription && Model.Requests > 12) Model.Requests = 12;
    }
    private Task SaveAsync() => RunAsync(async () =>
    {
        Apply(await Client.SaveCodingSettingsAsync(new(Revision, new CodingSettings
        {
            Enabled = Model.Enabled, Repository = Model.Repository.Trim(), Checkpoint = Model.Checkpoint.Trim(), ImageId = Model.ImageId.Trim(),
            GitHubAppId = Model.AppId, InstallationId = Model.InstallationId, MaxMinutes = Model.Minutes, MaxModelRequests = Model.Requests,
            VerifiedRulesetId = Model.RulesetId, VerifiedRulesetUpdatedAt = Model.RulesetUpdatedAt.Trim(),
            ModelBudgetUsd = Model.Budget, TestProject = Model.TestProject.Trim(), TestFilter = Model.TestFilter.Trim(),
            ModelId = Model.ModelId
        }, Model.RouterAction, Model.RouterAction == "replace" ? Model.RouterKey : null,
            Model.GitHubAction, Model.GitHubAction == "replace" ? Model.GitHubKey : null), Lifetime.Token));
        Notice = "Saved. New jobs use these settings; disabling takes effect on current model requests and publication.";
    });
    private void Apply(CodingSettingsView V)
    {
        Revision = V.Revision; Loaded = true; HasOpenRouterKey = V.HasOpenRouterKey; HasGitHubKey = V.HasGitHubPrivateKey;
        var S = V.Settings;
        Model = new() { Enabled = S.Enabled, Repository = S.Repository, Checkpoint = S.Checkpoint, ImageId = S.ImageId,
            AppId = S.GitHubAppId, InstallationId = S.InstallationId, Minutes = S.MaxMinutes, Requests = S.MaxModelRequests,
            RulesetId = S.VerifiedRulesetId, RulesetUpdatedAt = S.VerifiedRulesetUpdatedAt,
            Budget = S.ModelBudgetUsd, TestProject = S.TestProject, TestFilter = S.TestFilter };
        Model.ModelId = S.ModelId;
    }
    private async Task RunAsync(Func<Task> Action)
    {
        if (Busy) return;
        Busy = true; Error = Notice = null;
        try { await Action(); }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (HttpRequestException E)
        {
            Error = E.StatusCode switch
            {
                HttpStatusCode.Forbidden => "Deployment administrator access is required.",
                HttpStatusCode.Conflict => "Settings changed. Reload before saving.",
                HttpStatusCode.BadRequest => "Check the selected model belongs to your connected ChatGPT account, repository, checkpoint/image, App IDs, PEM key, test project and limits. ChatGPT allows 1–1000 requests; OpenRouter budget must cover $0.40 per permitted request (1–12). Disable before removing keys.",
                HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed => "Deploy the matching API version before configuring coding.",
                _ => "Coding settings are unavailable. Check the API and retry."
            };
            if (E.StatusCode == HttpStatusCode.Forbidden) { Loaded = false; Model = new(); }
        }
        finally { Model.RouterKey = Model.GitHubKey = ""; Busy = false; }
    }
    public async ValueTask DisposeAsync() { await Lifetime.CancelAsync(); Lifetime.Dispose(); }
    private sealed class FormModel
    {
        public bool Enabled { get; set; }
        public string ModelId { get; set; } = CodingJobs.Model;
        public string Repository { get; set; } = "memarino92/AgentPlayground";
        public string Checkpoint { get; set; } = "";
        public string ImageId { get; set; } = "";
        public long AppId { get; set; }
        public long InstallationId { get; set; }
        public long RulesetId { get; set; }
        public string RulesetUpdatedAt { get; set; } = "";
        public int Minutes { get; set; } = 20;
        public int Requests { get; set; } = 12;
        public decimal Budget { get; set; } = 5;
        public string TestProject { get; set; } = "PersonalAgent.Api.Tests/PersonalAgent.Api.Tests.csproj";
        public string TestFilter { get; set; } = "FullyQualifiedName~AgentSkillsTests";
        public string RouterAction { get; set; } = "keep";
        public string RouterKey { get; set; } = "";
        public string GitHubAction { get; set; } = "keep";
        public string GitHubKey { get; set; } = "";
    }
}
