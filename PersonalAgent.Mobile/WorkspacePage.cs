using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using PersonalAgent.Mobile.Services;
using PersonalAgent.Presentation;

namespace PersonalAgent.Mobile;

internal sealed partial class WorkspacePage : ContentPage
{
    private readonly NativeChatClient _client;
    private readonly Guid _id;
    private readonly Guid _run;
    private readonly int? _startMs;
    private CoachCheckinAdminItemResponse? _recording;
    private readonly VerticalStackLayout _rows = new() { Padding = 16, Spacing = 12 };
    private readonly Label _status = new() { Margin = new Thickness(16, 4) };
    private string? _filter;
    private int _offset;
    private DateTimeOffset? _before;
    private Guid _cursor;
    private bool _busy;
    private bool _loaded;

    public WorkspacePage(NativeChatClient Client, string Screen, Guid Id = default, Guid Run = default, int? StartMs = null,
        PersonalAgent.Presentation.CoachCheckinAdminItemResponse? Recording = null)
    {
        _client = Client;
        _id = Id;
        _run = Run;
        _startMs = StartMs;
        _recording = Recording;
        Title = Screen;
        var layout = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)], SafeAreaEdges = SafeAreaEdges.All };
        layout.Add(_status);
        layout.Add(new ScrollView { Content = _rows }, 0, 1);
        Content = layout;
        var refresh = new ToolbarItem { Text = "Refresh" };
        refresh.Clicked += async (_, _) => await ReloadAsync();
        ToolbarItems.Add(refresh);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_loaded) await ReloadAsync();
    }

    private Task ReloadAsync() => GuardAsync(RenderAsync);

    private async Task GuardAsync(Func<Task> Work)
    {
        if (_busy) return;
        _busy = true;
        _rows.IsEnabled = false;
        _status.Text = "Loading…";
        try { await Work(); _loaded = true; _status.Text = string.Empty; }
        catch (HttpRequestException ex) { _status.Text = ex.StatusCode switch
        {
            System.Net.HttpStatusCode.Forbidden => "Access denied for this account.",
            System.Net.HttpStatusCode.Unauthorized => "Session expired. Return to Chat to sign in.",
            System.Net.HttpStatusCode.Conflict => "This item changed. Refresh before trying again.",
            _ => "Request failed. Refresh to check the current state."
        }; }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _busy = false; _rows.IsEnabled = true; }
    }

    private void Button(string Text, Func<Task> Action)
    {
        var button = new Button { Text = Text, HorizontalOptions = LayoutOptions.Fill };
        button.Clicked += async (_, _) => await GuardAsync(Action);
        _rows.Add(button);
    }

    private void Link(string Screen, Guid Id = default, string? Caption = null, Guid Run = default) =>
        Button(Caption ?? Screen, () => Navigation.PushAsync(new WorkspacePage(_client, Screen, Id, Run)));

    private void Label(string Text, bool Heading = false) => _rows.Add(new Label
    {
        Text = Text, FontSize = Heading ? 18 : 15, FontAttributes = Heading ? FontAttributes.Bold : FontAttributes.None
    });

    private static string Text(JsonNode? Item, string Key) => Item?[Key]?.ToString() ?? string.Empty;
    private static Guid IdOf(JsonNode? Item, string Key = "id") => Guid.Parse(Text(Item, Key));
    private static string Human(string Name) => Regex.Replace(char.ToUpperInvariant(Name[0]) + Name[1..], "(?<=[a-z])(?=[A-Z])", " ");

    private void Details(JsonNode? Node, string? Name = null)
    {
        if (Node is null) return;
        if (Node is JsonObject obj)
        {
            if (Name is not null) Label(Human(Name), true);
            foreach (var field in obj) Details(field.Value, field.Key);
        }
        else if (Node is JsonArray list)
        {
            if (list.Count > 0 && Name is not null) Label(Human(Name), true);
            foreach (var item in list) Details(item);
        }
        else Label(Name is null ? Node.ToString() : $"{Human(Name)} · {Node}");
    }

    private Task<JsonNode?> Read(string Operation) => _client.WorkspaceAsync(Operation, _id, Offset: _offset, Run: _run);

    private async Task Change(string Operation, string? Value = null, JsonNode? Data = null, long Sequence = 0, Guid? Id = null)
    {
        if (!await DisplayAlertAsync(Title, "Apply this change?", "Apply", "Cancel")) return;
        await _client.WorkspaceAsync(Operation, Id ?? _id, Value, Data, Sequence);
        await RenderAsync();
    }

    private async Task RenderAsync()
    {
        _rows.Clear();
        switch (Title)
        {
            case "Administration":
                foreach (var section in PersonalAgent.Presentation.SettingsSection.All) Link(section.Title);
                break;
            case "Integrations and coach access":
                Link("Tool access"); Link("Coach assignments");
                break;
            case "Scheduled jobs":
                Button("Filter by status", async () => { _filter = await DisplayPromptAsync(Title, "Status (empty for all)"); _before = null; await RenderAsync(); });
                var jobs = (await _client.WorkspaceAsync("jobs", Value: _filter, Before: _before))!.AsArray();
                foreach (var job in jobs) Link("Scheduled job", IdOf(job, "taskId"), $"{Text(job, "status")} · {Text(job, "instruction")} · {Text(job, "executeAt")}");
                if (jobs.Count > 0) Button("Older jobs", async () => { _before = DateTimeOffset.Parse(Text(jobs[^1], "createdAt")); await RenderAsync(); });
                else Label("No jobs found.");
                break;
            case "Scheduled job":
                var jobDetail = await Read("job"); Details(jobDetail);
                if (Text(jobDetail?["job"], "status").Equals("Scheduled", StringComparison.OrdinalIgnoreCase) || Text(jobDetail?["job"], "status").Equals("Retrying", StringComparison.OrdinalIgnoreCase))
                    Button("Cancel job", () => Change("cancel-job"));
                break;
            case "Automations":
                var recipes = (await Read("automations"))!.AsArray();
                foreach (var recipe in recipes) Link("Automation", IdOf(recipe), $"{Text(recipe, "name")} · {Text(recipe, "status")}");
                Pages(recipes.Count, 50); break;
            case "Automation":
                var detail = await Read("automation"); Details(detail?["automation"]); Details(detail?["versions"], "versions"); Details(detail?["upcomingRuns"], "upcomingRuns");
                foreach (var run in detail?["runs"]?.AsArray() ?? []) Link("Automation run", _id, $"{Text(run, "scheduledAt")} · {Text(run, "status")}", IdOf(run));
                Pages(detail?["runs"]?.AsArray().Count ?? 0, 50);
                Button("Run now", () => Change("control", "run"));
                var paused = Text(detail?["automation"], "status") == "Paused";
                Button(paused ? "Resume" : "Pause", () => Change("control", paused ? "resume" : "pause"));
                break;
            case "Automation run":
                var execution = await Read("run");
                if (execution?["sandboxes"]?.AsArray().Any(s => s?["artifacts"] is JsonArray { Count: > 0 }) == true)
                    Button("Open charts and files", () => Browser.Default.OpenAsync(_client.AutomationRunUri(_id, _run), BrowserLaunchMode.SystemPreferred));
                Details(execution);
                break;
            case "Coding jobs":
                Link("Outstanding questions");
                var codingJobs = (await Read("coding-jobs"))!.AsArray();
                foreach (var item in codingJobs)
                    Link("Coding job", IdOf(item), $"{Text(item, "instruction")} · {item?["displayStatus"]?.ToString() ?? Text(item, "status")}");
                if (codingJobs.Count == 0) Label("No coding jobs.");
                break;
            case "Outstanding questions":
                var questions = (await Read("coding-questions"))!.AsArray();
                if (questions.Count == 0) Label("No coding jobs are waiting for an answer.");
                foreach (var question in questions)
                {
                    var questionId = IdOf(question);
                    var prompt = Text(question, "text");
                    Label(prompt, true);
                    if (DateTimeOffset.TryParse(Text(question, "deadline"), out var deadline))
                        Label($"Job deadline {deadline.ToLocalTime():g}");
                    Link("Conversation history", IdOf(question, "sessionId"), "Open originating chat");
                    Button("Answer question", async () =>
                    {
                        var answer = await DisplayPromptAsync("OpenCode question", prompt, "Send answer", "Cancel", maxLength: 4000);
                        if (string.IsNullOrWhiteSpace(answer)) return;
                        await _client.WorkspaceAsync("answer-coding-question", questionId, answer);
                        await RenderAsync();
                    });
                }
                break;
            case "Coding job":
                var codingJob = await Read("coding-job");
                var codingStatus = Text(codingJob, "status");
                if (codingJob is JsonObject codingDetails && codingJob["displayStatus"]?.ToString() is { } label)
                {
                    codingDetails["status"] = label;
                    codingDetails.Remove("displayStatus");
                }
                var canCancel = codingJob?["canCancel"]?.GetValue<bool>() ?? codingStatus is not ("PrOpened" or "Failed" or "Cancelled" or "Publishing");
                (codingJob as JsonObject)?.Remove("canCancel");
                Details(codingJob);
                if (Uri.TryCreate(Text(codingJob, "pullRequestUrl"), UriKind.Absolute, out var pr) && pr.Scheme == "https")
                    Button("Review pull request", () => Browser.Default.OpenAsync(pr, BrowserLaunchMode.SystemPreferred));
                if (canCancel) Button("Cancel job", () => Change("cancel-coding"));
                break;
            case "Coach recordings":
                if (_client.IsOwner) Button("Upload recording", () => TransferAsync("upload"));
                var recordings = (await Read("recordings"))!.Deserialize<List<CoachCheckinAdminItemResponse>>(JsonSerializerOptions.Web) ?? [];
                foreach (var recording in recordings)
                    Button($"{RecordingLabel.FromFileName(recording.OriginalFileName)} · {recording.Status}",
                        () => Navigation.PushAsync(new WorkspacePage(_client, "Recording details", recording.UploadId, Recording: recording)));
                if (recordings.Count == 0) Label("No recordings.");
                break;
            case "Recording details":
                var queue = (await Read("recordings"))!.Deserialize<List<CoachCheckinAdminItemResponse>>(JsonSerializerOptions.Web);
                _recording = queue?.FirstOrDefault(recording => recording.UploadId == _id);
                if (_recording is null) { Label("Recording unavailable."); break; }
                Label(RecordingLabel.FromFileName(_recording.OriginalFileName), true);
                Label($"{_recording.ProfileId} · {_recording.Status} · {_recording.UpdatedAtUtc.ToLocalTime():g}");
                if (_recording.Error is not null) Label(_recording.Error);
                if (_recording.ExecutiveSummary is not null) Label(_recording.ExecutiveSummary);
                var overrides = new JsonArray();
                foreach (var speaker in _recording.SpeakerLabels)
                {
                    Label($"Speaker {speaker.SpeakerLabel} · {speaker.SpeakerRole} · {speaker.UtteranceCount} turns");
                    foreach (var sample in speaker.SampleTexts) Label(sample);
                    if (!_client.IsOwner) continue;
                    var edit = new JsonObject { ["speakerLabel"] = speaker.SpeakerLabel, ["role"] = speaker.SpeakerRole };
                    overrides.Add(edit);
                    var role = new Picker { Title = "Speaker role", ItemsSource = new[] { "coach", "athlete" }, SelectedItem = speaker.SpeakerRole };
                    role.SelectedIndexChanged += (_, _) => edit["role"] = role.SelectedItem?.ToString();
                    _rows.Add(role);
                }
                if (overrides.Count > 0) Button("Apply speaker roles", () => Change("override", Data: new JsonObject { ["overrides"] = overrides.DeepClone() }));
                Link("Recording evidence", _id, "Open transcript and audio"); break;
            case "Recording evidence": await EvidenceAsync(); break;
            case "Check-in preview":
                Label("Preview recordings", true);
                foreach (var item in PersonalAgent.Presentation.RecordingPreview.Recordings())
                    Button($"{item.OriginalFileName} · {item.Status}", () => Navigation.PushAsync(new WorkspacePage(_client, "Preview transcript", Recording: item)));
                break;
            case "Preview transcript":
                if (_recording is not null)
                {
                    Label(_recording.OriginalFileName, true);
                    Label($"{_recording.Status} · {_recording.ProfileId}");
                    foreach (var speaker in _recording.SpeakerLabels) Label($"Speaker {speaker.SpeakerLabel} · {speaker.SpeakerRole} · {speaker.UtteranceCount} turns");
                }
                Label("Sample transcript", true);
                foreach (var turn in PersonalAgent.Presentation.RecordingPreview.BuildPreviewUtterances())
                    Label($"{TimeSpan.FromMilliseconds(turn.StartMs):g} · {turn.SpeakerRole}\n{turn.Text}");
                break;
            case "Chat options":
                if (_id == Guid.Empty) { Label("Open a conversation first."); break; }
                var models = await Read("models");
                foreach (var model in models?["models"]?.AsArray() ?? []) Button(Text(model, "displayName"), () => Change("set-model", Text(model, "id")));
                Button("Start fresh", () => Change("clear")); break;
            case "Chat history":
                Button("Search messages", async () =>
                {
                    var query = await DisplayPromptAsync(Title, "Search conversation history");
                    if (string.IsNullOrWhiteSpace(query)) return;
                    var matches = await _client.WorkspaceAsync("search", Value: query);
                    _rows.Clear(); foreach (var match in matches!.AsArray()) Link("Conversation history", IdOf(match, "sessionId"), Text(match, "excerpt"));
                    if (matches.AsArray().Count == 0) Label("No matches.");
                });
                var page = await _client.WorkspaceAsync("history", _cursor, Before: _before);
                foreach (var session in page?["sessions"]?.AsArray() ?? []) Link("Conversation history", IdOf(session, "sessionId"), Text(session, "snippet"));
                if (page?["hasMore"]?.GetValue<bool>() == true) Button("Older conversations", async () =>
                { _before = DateTimeOffset.Parse(Text(page, "nextBeforeActivityAt")); _cursor = IdOf(page, "nextBeforeSessionId"); await RenderAsync(); });
                break;
            case "Conversation history": Details((await Read("session"))?["messages"]); break;
            default: await SettingsAsync(); break;
        }
    }

    private async Task ListAsync(string Operation, string Screen, string Name, string Id = "id")
    {
        var records = (await Read(Operation))!.AsArray();
        foreach (var item in records) Link(Screen, IdOf(item, Id), $"{Text(item, Name)} · {Text(item, "status")}");
        if (records.Count == 0) Label("No items.");
    }

    private void Pages(int Count, int Size)
    {
        if (Count == 0) Label("No items.");
        if (_offset > 0) Button("Newer", async () => { _offset = Math.Max(0, _offset - Size); await RenderAsync(); });
        if (Count >= Size) Button("Older", async () => { _offset += Size; await RenderAsync(); });
    }
}
