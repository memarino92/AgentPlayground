using System.Security.Claims;
using System.Text.Json;

using PersonalAgent.Contracts;
using PersonalAgent.Contracts.Automations;
using PersonalAgent.Contracts.Coding;
using PersonalAgent.Integrations;
using PersonalAgent.Web.Services;

namespace PersonalAgent.Web.Endpoints;

internal static class NativeWorkspaceEndpoints
{
    internal sealed record Input(string? ProfileId, Guid Id = default, Guid Run = default, int Offset = 0,
        string? Value = null, long Sequence = 0, DateTimeOffset? Before = null, JsonElement Data = default)
    {
        public T Body<T>() => Data.ValueKind == JsonValueKind.Object && Data.Deserialize<T>(JsonSerializerOptions.Web) is { } result
            ? result : throw new JsonException();
    }

    internal static void MapNativeWorkspace(this WebApplication App) =>
        App.MapPost("/mobile/workspace/{Operation}", ExecuteAsync);

    internal static async Task<IResult> ExecuteAsync(string Operation, Input Request, HttpContext Context,
        MobileChatGrant Grants, PersonalAgentClient Client, CancellationToken Token)
    {
        Context.Response.Headers.CacheControl = "no-store";
        var User = Grants.Read(Context.Request.Headers.Authorization.ToString());
        if (User is null) return Results.Unauthorized();
        var Owner = User.IsInRole("Owner");
        var Actor = Owner ? User.FindFirstValue("urn:github:login")! : $"google:{User.FindFirstValue(ClaimTypes.NameIdentifier)}";
        Client.UseMobileAccount(User);
        try
        {
            var Profiles = Owner ? new[] { Actor } : await Client.GetAssignedProfilesAsync(Actor, User.FindFirstValue(ClaimTypes.Email)!, Token);
            if (Operation == "account") return Results.Ok(new { Owner, Profiles });
            var Profile = Owner ? Actor : Request.ProfileId;
            if (Profile is null || !Profiles.Contains(Profile, StringComparer.OrdinalIgnoreCase)) return Results.StatusCode(403);
            var Id = Request.Id;
            var Value = Request.Value ?? string.Empty;
            switch (Operation)
            {
                case "jobs": return Results.Ok(await Client.GetJobsAsync(Profile, Request.Value, Request.Before, Token));
                case "job": return Results.Ok(await Client.GetJobAsync(Id, Token));
                case "cancel-job": return Results.Ok(await Client.CancelJobAsync(Id, Token));
                case "automations": return Results.Ok(await Client.GetAutomationsAsync(Profile, Request.Offset, Token));
                case "automation": return Results.Ok(await Client.GetAutomationAsync(Profile, Id, Request.Offset, Token));
                case "run": return Results.Ok(await Client.GetAutomationRunAsync(Profile, Id, Request.Run, Token));
                case "control" when Value is "run" or "pause" or "resume":
                    await Client.ControlAutomationAsync(Profile, Id, Value, Token); break;
                case "recordings": return Results.Ok(await Client.GetCoachCheckinItemsAsync(Profile));
                case "evidence": return Results.Ok(await Client.GetCoachEvidenceAsync(Id, Profile, Token));
                case "history": return Results.Ok(await Client.GetSessionsAsync(Profile, Request.Before, Id == Guid.Empty ? null : Id,
                    actorId: Actor, role: Owner ? "Owner" : "Coach"));
                case "session": return Results.Ok(await Client.GetHistoryAsync(Id.ToString(), Profile, Actor, Owner ? "Owner" : "Coach"));
                case "search": return Results.Ok(await Client.SearchConversationHistoryAsync(Profile, Value));
                case "models": return Results.Ok(await Client.GetModelsAsync());
                case "set-model": await Client.SetChatModelAsync(Id.ToString(), Profile, Value); break;
                case "clear": await Client.ClearConversationAsync(Id.ToString(), Profile); break;
                case "card": return Results.Ok(await Client.UpdateChatCardAsync(Id.ToString(), Profile, Request.Sequence, Value, Request.Body<ChatCardAction>()));
                default:
                    if (!Owner) return Results.StatusCode(403);
                    return await OwnerActionAsync(Operation, Request, Client, Profile, Token);
            }
            return Results.Ok(true);
        }
        catch (HttpRequestException Error) { return Results.StatusCode((int?)Error.StatusCode ?? 502); }
        catch (JsonException) { return Results.BadRequest(); }
    }

    private static async Task<IResult> OwnerActionAsync(string Operation, Input R, PersonalAgentClient C, string Profile, CancellationToken Token)
    {
        switch (Operation)
        {
            case "coding-jobs": return Results.Ok(await C.GetCodingJobsAsync(Profile, Token));
            case "coding-questions": return Results.Ok(await C.GetCodingQuestionsAsync(Profile, null, Token));
            case "coding-updates": return Results.Ok(await C.HasCodingMessagesAsync(Profile, R.Id, R.Sequence, Token));
            case "answer-coding-question": await C.AnswerCodingQuestionAsync(Profile, R.Id, R.Value ?? "", Token); break;
            case "coding-job": return Results.Ok(await C.GetCodingJobAsync(Profile, R.Id, Token));
            case "cancel-coding": await C.CancelCodingJobAsync(Profile, R.Id, Token); break;
            case "coding-settings": return Results.Ok(await C.GetCodingSettingsAsync(Token));
            case "coding-models": return Results.Ok(await C.GetCodingModelsAsync(Token));
            case "save-coding": return Results.Ok(await C.SaveCodingSettingsAsync(R.Body<SaveCodingSettings>(), Token));
            case "runtime": return Results.Ok(await C.GetAutomationRuntimeAsync(Token));
            case "save-runtime": return Results.Ok(await C.SaveAutomationRuntimeAsync(R.Body<SaveAutomationRuntime>(), Token));
            case "database": return Results.Ok(await C.GetDatabaseSettingsAsync(Token));
            case "save-database": await C.SaveDatabaseSettingsAsync(R.Body<SaveDatabaseSettingsRequest>(), Token); break;
            case "reload-credentials": return Results.Ok(await C.ReloadDatabaseCredentialsAsync(Token));
            case "provider": await C.SaveOpenRouterCredentialAsync(R.Body<SaveProviderCredentialRequest>(), Token); return Results.Ok(await C.ReloadDatabaseCredentialsAsync(Token));
            case "backup": return Results.Ok(await C.GetBackupSettingsAsync(Token));
            case "save-backup": await C.SaveBackupSettingsAsync(R.Body<SaveBackupSettingsRequest>(), Token); break;
            case "tools": return Results.Ok(await C.GetToolAccessAsync());
            case "save-tools": await C.SaveToolAccessAsync(R.Body<ToolEdits>().Permissions, Profile); break;
            case "assignments": return Results.Ok(await C.GetCoachAssignmentsAsync(Profile));
            case "assign": var assignment = R.Body<Assignment>(); await C.SaveCoachAssignmentAsync(assignment.CoachEmail, Profile, assignment.IsActive, Profile); break;
            case "override": var edit = R.Body<SpeakerEdits>(); await C.ApplySpeakerOverridesAsync(R.Id, Profile, edit.Overrides); break;
            case "connections": return Results.Ok(await C.GetChatGptConnectionsAsync(Token));
            case "pair": return Results.Ok(await C.StartChatGptPairingAsync(R.Id == Guid.Empty ? null : R.Id, Token));
            case "disconnect": return Results.Ok(await C.DisconnectChatGptAsync(R.Id, Token));
            case "sentry": return Results.Ok(await C.GetIntegrationSettingsAsync(Token));
            case "otel": return Results.Ok(await C.GetOtelSettingsAsync(Token));
            case "save-sentry": return Results.Ok(await C.SaveIntegrationSettingsAsync(R.Body<SaveIntegrationRequest>(), Token));
            case "save-otel": return Results.Ok(await C.SaveOtelSettingsAsync(R.Body<SaveIntegrationRequest>(), Token));
            case "apply-sentry": return Results.Ok(await C.ApplyIntegrationSettingsAsync(R.Sequence, Token));
            case "apply-otel": return Results.Ok(await C.ApplyOtelSettingsAsync(R.Sequence, Token));
            case "reload-sentry": return Results.Ok(await C.ReloadIntegrationSettingsAsync(Token));
            case "reload-otel": return Results.Ok(await C.ReloadOtelSettingsAsync(Token));
            case "test-sentry": return Results.Ok(await C.TestIntegrationSettingsAsync(R.Sequence, Token));
            default: return Results.NotFound();
        }
        return Results.Ok(true);
    }

    private sealed record Assignment(string CoachEmail, bool IsActive);
    private sealed record ToolEdits(List<ToolRolePermissionResponse> Permissions);
    private sealed record SpeakerEdits(List<SpeakerOverrideItem> Overrides);
}
