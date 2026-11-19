using System.Text.Json;
using System.Text.Json.Nodes;

namespace PersonalAgent.Mobile;

internal sealed partial class WorkspacePage
{
    private Entry Field(string Name, string? Initial = null, bool Secret = false)
    {
        Label(Name);
        var field = new Entry { Text = Initial, IsPassword = Secret, IsTextPredictionEnabled = !Secret };
        _rows.Add(field);
        return field;
    }

    private void EditFields(JsonObject Values)
    {
        foreach (var pair in Values.ToArray())
        {
            if (pair.Value is not JsonValue value) continue;
            var key = pair.Key;
            var kind = value.GetValueKind();
            if (kind == JsonValueKind.True || kind == JsonValueKind.False)
            {
                var toggle = new Switch { IsToggled = value.GetValue<bool>() };
                Label(Human(key)); _rows.Add(toggle);
                toggle.Toggled += (_, args) => Values[key] = args.Value;
            }
            else
            {
                var field = Field(Human(key), value.ToString());
                field.TextChanged += (_, args) =>
                {
                    if (kind != JsonValueKind.Number) Values[key] = args.NewTextValue;
                    else Values[key] = decimal.TryParse(args.NewTextValue, System.Globalization.CultureInfo.InvariantCulture, out var number)
                        ? JsonValue.Create(number) : JsonValue.Create(args.NewTextValue);
                };
            }
        }
    }

    private async Task SettingsAsync()
    {
        switch (Title)
        {
            case "Sentry": case "OpenTelemetry":
                var op = Title == "Sentry" ? "sentry" : "otel";
                var settings = (await Read(op))!;
                var edits = new JsonObject();
                foreach (var item in settings["fields"]!.AsArray())
                {
                    var key = Text(item, "key");
                    var secret = item!["secret"]!.GetValue<bool>();
                    var entry = Field(Text(item, "label"), secret ? null : Text(settings["values"], key), secret);
                    if (secret) entry.Placeholder = "Leave untouched to retain saved value";
                    entry.TextChanged += (_, e) => edits[key] = e.NewTextValue;
                    if (!secret) edits[key] = entry.Text;
                }
                var saved = settings["savedRevision"]!.GetValue<long>();
                Button("Save", () => Change("save-" + op, Data: new JsonObject { ["expectedRevision"] = saved, ["values"] = edits.DeepClone() }));
                Button("Apply saved revision", () => Change("apply-" + op, Sequence: saved));
                Button("Reload", () => Change("reload-" + op));
                if (op == "sentry") Button("Send test event", async () => Details(await _client.WorkspaceAsync("test-sentry", Sequence: settings["activeRevision"]!.GetValue<long>())));
                Details(settings["configuredSecrets"], "configuredSecrets"); Details(settings["instances"], "instances"); break;
            case "Railway sandboxes": case "Platform coding":
                var coding = Title == "Platform coding";
                var view = (await Read(coding ? "coding-settings" : "runtime"))!;
                var values = view["settings"]!.AsObject(); EditFields(values);
                if (coding) Details(await Read("coding-models"), "availableModels");
                var body = new JsonObject { ["expectedRevision"] = view["revision"]!.DeepClone(), ["settings"] = values.DeepClone() };
                foreach (var key in coding ? new[] { "openRouterKey", "gitHubPrivateKey" } : new[] { "token" })
                {
                    var password = Field(Human(key), Secret: true);
                    var action = new Picker { Title = $"{Human(key)} action", ItemsSource = new[] { "keep", "replace", "clear" }, SelectedIndex = 0 };
                    _rows.Add(action); action.SelectedIndexChanged += (_, _) => body[key + "Action"] = action.SelectedItem?.ToString();
                    password.TextChanged += (_, e) => body[key] = e.NewTextValue;
                }
                Button("Save", () =>
                {
                    body["settings"] = values.DeepClone();
                    return Change(coding ? "save-coding" : "save-runtime", Data: body);
                }); break;
            case "Database backups":
                var backup = (await Read("backup"))!.AsObject();
                var configured = backup["hasPassword"]?.GetValue<bool>() == true;
                backup.Remove("hasPassword"); EditFields(backup);
                var backupPassword = Field(configured ? "Replace saved password" : "Password", Secret: true);
                Button("Save", () => { backup["password"] = string.IsNullOrEmpty(backupPassword.Text) ? null : backupPassword.Text; return Change("save-backup", Data: backup); }); break;
            case "Database settings":
                var db = (await Read("database"))!.AsArray();
                foreach (var row in db)
                    Button($"{Text(row, "scope")} · {Text(row, "key")}", async () =>
                    {
                        _rows.Clear();
                        var replacement = Field(Text(row, "key"), Text(row, "value"), row!["isSecret"]!.GetValue<bool>());
                        var active = new Switch { IsToggled = row["isActive"]!.GetValue<bool>() }; Label("Active"); _rows.Add(active);
                        Button("Save", () =>
                        {
                            var update = row.DeepClone(); update["value"] = row["isSecret"]!.GetValue<bool>() && string.IsNullOrEmpty(replacement.Text) ? null : replacement.Text;
                            update["isActive"] = active.IsToggled;
                            return Change("save-database", Data: new JsonObject { ["changes"] = new JsonArray(update) });
                        }); await Task.CompletedTask;
                    });
                Button("Reload credentials", () => Change("reload-credentials")); break;
            case "AI providers":
                var database = (await Read("database"))!.AsArray();
                var provider = database.FirstOrDefault(row => Text(row, "scope") == "Api" && Text(row, "key") == "OpenRouter:ApiKey");
                var apiKey = Field("OpenRouter API key", Secret: true);
                Button("Save OpenRouter key", () => Change("provider", Data: new JsonObject { ["expectedVersion"] = provider?["version"]?.DeepClone(), ["value"] = apiKey.Text }));
                Label("ChatGPT connections", true);
                async Task Pair(Guid id)
                {
                    var pair = await _client.WorkspaceAsync("pair", id);
                    await DisplayAlertAsync("Connect ChatGPT", $"Run the ChatGptConnect helper on your computer and enter {Text(pair, "code")}.", "Done");
                }
                Button("Connect ChatGPT", () => Pair(Guid.Empty));
                foreach (var account in (await Read("connections"))!.AsArray())
                {
                    var id = IdOf(account); Label(Text(account, "label"));
                    Button("Reconnect", () => Pair(id)); Button("Disconnect", () => Change("disconnect", Id: id));
                }
                break;
            case "Tool access":
                var catalog = (await Read("tools"))!;
                var permissions = catalog["permissions"]!.AsArray();
                foreach (var tool in catalog["tools"]!.AsArray())
                {
                    var key = Text(tool, "key");
                    Label(Text(tool, "displayName"), true);
                    Label(Text(tool, "description"));
                    foreach (var role in new[] { "Owner", "Coach" })
                    {
                        var permission = permissions.FirstOrDefault(p => Text(p, "role") == role && Text(p, "toolKey") == key);
                        if (permission is null)
                        {
                            permission = new JsonObject { ["role"] = role, ["toolKey"] = key, ["isEnabled"] = false };
                            permissions.Add(permission);
                        }
                        Label(role);
                        var toggle = new Switch { IsToggled = permission["isEnabled"]!.GetValue<bool>(), IsEnabled = tool!["isAvailable"]!.GetValue<bool>() };
                        _rows.Add(toggle);
                        toggle.Toggled += (_, e) => permission["isEnabled"] = e.Value;
                    }
                }
                Button("Save permissions", () => Change("save-tools", Data: new JsonObject { ["permissions"] = permissions.DeepClone() })); break;
            case "Coach assignments":
                var email = Field("Coach email"); Button("Assign coach", () => Change("assign", Data: new JsonObject { ["coachEmail"] = email.Text, ["isActive"] = true }));
                foreach (var row in (await Read("assignments"))!.AsArray())
                {
                    var address = Text(row, "coachEmail"); var enabled = row!["isActive"]!.GetValue<bool>();
                    Button($"{(enabled ? "Deactivate" : "Activate")} {address}", () => Change("assign", Data: new JsonObject { ["coachEmail"] = address, ["isActive"] = !enabled }));
                }
                break;
        }
    }
}
