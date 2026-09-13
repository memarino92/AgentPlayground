using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.JSInterop;
using PersonalAgent.Web.Services;

namespace PersonalAgent.Web.Components.Pages;

public partial class CoachCheckinsAdmin
{
    [CascadingParameter] private Task<AuthenticationState> AuthenticationState { get; set; } = default!;

    private bool isLoading;
    private bool isApplyingOverride;
    private bool isDownloadingTranscript;
    private bool isUploading;
    private string? errorMessage;
    private string? uploadStatus;
    private IBrowserFile? selectedFile;
    private List<CoachCheckinAdminItemResponse> items = [];
    private readonly Dictionary<Guid, Dictionary<int, string>> overrideSelections = [];
    private readonly HashSet<(Guid UploadId, int SpeakerLabel)> editedRoles = [];
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private Guid? selectedUploadId;

    private CoachCheckinAdminItemResponse? SelectedItem => selectedUploadId is null
        ? null
        : items.FirstOrDefault(item => item.UploadId == selectedUploadId.Value);

    [SupplyParameterFromQuery(Name = "uploadId")] public Guid? UploadId { get; set; }
    private string? profileId;
    private bool initialized;
    protected override async Task OnInitializedAsync()
    {
        var user = (await AuthenticationState).User;
        profileId = user.IsInRole("Owner") ? user.FindFirst("urn:github:login")?.Value
            : (await ApiClient.GetAssignedProfilesAsync($"google:{user.FindFirst(ClaimTypes.NameIdentifier)?.Value}", user.FindFirst(ClaimTypes.Email)?.Value ?? "")).FirstOrDefault();
        await LoadAsync();
        initialized = true;
    }

    protected override void OnParametersSet()
    {
        if (!initialized) return;
        var requested = UploadId ?? items.FirstOrDefault()?.UploadId;
        if (requested == selectedUploadId) return;
        selectedUploadId = requested;
    }

    private async Task LoadAsync()
    {
        await refreshGate.WaitAsync();
        isLoading = true;
        errorMessage = null;
        try
        {
            items = string.IsNullOrWhiteSpace(profileId) ? [] : await ApiClient.GetCoachCheckinItemsAsync(profileId);
            Logger.LogInformation("Loaded {Count} coach check-in admin items", items.Count);

            foreach (var item in items)
            {
                var refreshedSelections = item.SpeakerLabels
                    .GroupBy(label => label.SpeakerLabel)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Select(label => NormalizeRole(label.SpeakerRole)).Distinct().Count() == 1
                            ? group.Select(label => NormalizeRole(label.SpeakerRole)).First()
                            : "unknown");
                if (overrideSelections.TryGetValue(item.UploadId, out var existing))
                    foreach (var entry in existing.Where(entry => editedRoles.Contains((item.UploadId, entry.Key))))
                        refreshedSelections[entry.Key] = entry.Value;
                overrideSelections[item.UploadId] = refreshedSelections;
            }
            foreach (var removedId in overrideSelections.Keys.Where(id => items.All(item => item.UploadId != id)).ToList())
            {
                overrideSelections.Remove(removedId);
                editedRoles.RemoveWhere(entry => entry.UploadId == removedId);
            }

            if (selectedUploadId is null || items.All(item => item.UploadId != selectedUploadId.Value))
                selectedUploadId = UploadId ?? items.FirstOrDefault()?.UploadId;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load coach check-in admin items");
            errorMessage = ex.Message;
        }
        finally
        {
            isLoading = false;
            refreshGate.Release();
        }
    }

    private async Task ApplyOverrideAsync(CoachCheckinAdminItemResponse item)
    {
        if (isApplyingOverride) return;
        if (!overrideSelections.TryGetValue(item.UploadId, out var selectionMap) || selectionMap.Count is 0)
        {
            errorMessage = "No speaker override choices found for this upload.";
            return;
        }

        var overrides = selectionMap
            .OrderBy(entry => entry.Key)
            .Select(entry => new SpeakerOverrideItem(entry.Key, entry.Value))
            .ToList();

        isApplyingOverride = true;
        errorMessage = null;

        try
        {
            await ApiClient.ApplySpeakerOverridesAsync(item.UploadId, item.ProfileId, overrides);
            editedRoles.RemoveWhere(entry => entry.UploadId == item.UploadId);
            Logger.LogInformation("Applied speaker override for upload {UploadId}", item.UploadId);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to apply speaker override for upload {UploadId}", item.UploadId);
            errorMessage = ex.Message;
        }
        finally
        {
            isApplyingOverride = false;
        }
    }

    private string GetOverrideSelection(Guid uploadId, int speakerLabel, string? defaultRole)
    {
        if (!overrideSelections.TryGetValue(uploadId, out var selections))
        {
            selections = [];
            overrideSelections[uploadId] = selections;
        }

        if (!selections.TryGetValue(speakerLabel, out var selectedRole))
        {
            selectedRole = NormalizeRole(defaultRole);
            selections[speakerLabel] = selectedRole;
        }

        return selectedRole;
    }

    private string GetOverrideSelectionForSelectedItem(CoachCheckinSpeakerLabelInfoResponse label)
    {
        if (selectedUploadId is null) return NormalizeRole(label.SpeakerRole);
        return GetOverrideSelection(selectedUploadId.Value, label.SpeakerLabel, label.SpeakerRole);
    }

    private void SetOverrideSelection(Guid uploadId, int speakerLabel, string? role)
    {
        var normalizedRole = NormalizeRole(role);
        if (!overrideSelections.TryGetValue(uploadId, out var selections))
        {
            selections = [];
            overrideSelections[uploadId] = selections;
        }

        selections[speakerLabel] = normalizedRole;
        editedRoles.Add((uploadId, speakerLabel));
    }

    private Task RefreshLiveAsync() => LoadAsync();

    private static string NormalizeRole(string? role)
    {
        if (string.Equals(role, "coach", StringComparison.OrdinalIgnoreCase)) return "coach";
        if (string.Equals(role, "athlete", StringComparison.OrdinalIgnoreCase)) return "athlete";
        return "unknown";
    }

    private Task SelectUploadAsync(Guid uploadId)
    {
        selectedUploadId = uploadId;
        Navigation.NavigateTo(Navigation.GetUriWithQueryParameter("uploadId", uploadId));
        return Task.CompletedTask;
    }

    private async Task HandleRoleChanged(SpeakerRoleChanged change)
    {
        SetOverrideSelection(change.UploadId, change.SpeakerLabel, change.Role);
        await Task.CompletedTask;
    }

    private async Task DownloadTranscriptAsync(Guid uploadId)
    {
        isDownloadingTranscript = true;
        errorMessage = null;
        try
        {
            var download = await ApiClient.DownloadCoachCheckinTranscriptAsync(uploadId, profileId);
            var base64 = Convert.ToBase64String(download.Bytes);
            await JsRuntime.InvokeVoidAsync("downloadTextFileFromBase64", download.FileName, base64, "text/plain;charset=utf-8");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to download transcript for upload {UploadId}", uploadId);
            errorMessage = ex.Message;
        }
        finally
        {
            isDownloadingTranscript = false;
        }
    }

    private void HandleFileSelected(InputFileChangeEventArgs args)
    {
        if (args.FileCount > 0)
            selectedFile = args.File;
    }

    private async Task UploadAsync()
    {
        if (selectedFile is null) return;

        var authState = await AuthenticationState;
        var profileId = authState.User.FindFirst("urn:github:login")?.Value
            ?? authState.User.FindFirst(ClaimTypes.Name)?.Value;
        if (string.IsNullOrWhiteSpace(profileId))
        {
            uploadStatus = "Unable to determine the signed-in profile.";
            return;
        }

        isUploading = true;
        uploadStatus = null;
        try
        {
            await using var stream = selectedFile.OpenReadStream(Math.Max(selectedFile.Size, 1));
            await using var content = new MemoryStream();
            await stream.CopyToAsync(content);
            var upload = await ApiClient.UploadCoachCheckinAsync(profileId, selectedFile.Name, selectedFile.ContentType, content.ToArray());
            uploadStatus = upload is null ? "Upload failed." : $"Uploaded {selectedFile.Name}. It will appear in the queue as processing progresses.";
            selectedFile = null;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to upload coach check-in recording");
            uploadStatus = ex.Message;
        }
        finally
        {
            isUploading = false;
        }
    }

}
