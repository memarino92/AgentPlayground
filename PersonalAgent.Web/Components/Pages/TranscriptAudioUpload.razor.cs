using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using PersonalAgent.Web.Services;

namespace PersonalAgent.Web.Components.Pages;

public partial class TranscriptAudioUpload
{
    [Inject] private PersonalAgentClient Client { get; set; } = default!;
    [Parameter] public Guid UploadId { get; set; }
    [Parameter] public string ProfileId { get; set; } = string.Empty;
    [Parameter] public bool HasAudio { get; set; }
    [Parameter] public long MaxUploadBytes { get; set; } = 25 * 1024 * 1024;
    [Parameter] public EventCallback<Guid> OnUploaded { get; set; }
    private readonly CancellationTokenSource Lifetime = new();
    private bool Uploading;
    private string? Message, Error;

    private async Task UploadAsync(InputFileChangeEventArgs Event)
    {
        if (Uploading) return;
        var Id = UploadId;
        var Profile = ProfileId;
        var Limit = MaxUploadBytes;
        var Token = Lifetime.Token;
        Uploading = true;
        Error = null;
        Message = "Uploading audio…";
        try
        {
            await using var Audio = Event.File.OpenReadStream(Limit, Token);
            var ContentType = string.IsNullOrWhiteSpace(Event.File.ContentType) ? Path.GetExtension(Event.File.Name).ToLowerInvariant() switch
            {
                ".m4a" => "audio/mp4", ".mp3" => "audio/mpeg", ".wav" => "audio/wav",
                ".ogg" => "audio/ogg", ".webm" => "audio/webm", ".flac" => "audio/flac",
                _ => "application/octet-stream"
            } : Event.File.ContentType;
            await Client.AttachCoachAudioAsync(Id, Profile, Audio, ContentType, Token);
            if (Token.IsCancellationRequested) return;
            Message = "Audio uploaded.";
            await OnUploaded.InvokeAsync(Id);
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
        catch (Exception Exception) when (Exception is IOException or HttpRequestException or OperationCanceledException)
        {
            Message = null;
            Error = $"Audio could not be uploaded. Choose a non-empty file up to {Limit / (1024 * 1024)} MB and try again.";
        }
        finally { Uploading = false; }
    }

    public ValueTask DisposeAsync()
    {
        Lifetime.Cancel();
        Lifetime.Dispose();
        return ValueTask.CompletedTask;
    }
}
