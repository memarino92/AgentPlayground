using Android.Media;

namespace PersonalAgent.Mobile;

internal sealed partial class WorkspacePage
{
    private MediaPlayer? _player;
    private string? _audioPath;
    private int _playbackGeneration;

    protected override void OnDisappearing()
    {
        ReleasePlayer();
        base.OnDisappearing();
    }

    private void ReleasePlayer()
    {
        _playbackGeneration++;
        _player?.Release();
        _player?.Dispose();
        _player = null;
        if (_audioPath is not null) File.Delete(_audioPath);
        _audioPath = null;
    }

    private async Task TransferAsync(string Operation)
    {
        var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Select an M4A recording",
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>> { [DevicePlatform.Android] = ["audio/*"] }) });
        if (file is null) return;
        await using var stream = await file.OpenReadAsync();
        await _client.TransferAsync(Operation, _id, stream, null, file.FileName);
        await RenderAsync();
    }

    private async Task PlayAsync(int Start = 0)
    {
        if (_player is null)
        {
            var generation = _playbackGeneration;
            _audioPath = Path.Combine(FileSystem.CacheDirectory, $"{Guid.NewGuid():N}.m4a");
            try
            {
                await using (var file = File.Create(_audioPath)) await _client.TransferAsync("audio", _id, null, file);
                if (generation != _playbackGeneration) return;
                _player = new MediaPlayer();
                await _player.SetDataSourceAsync(_audioPath);
                if (generation != _playbackGeneration) return;
                await Task.Run(_player.Prepare);
                if (generation != _playbackGeneration) return;
            }
            catch { ReleasePlayer(); throw; }
        }
        _player.SeekTo(Start);
        _player.Start();
    }

    private async Task EvidenceAsync()
    {
        var evidence = await Read("evidence") ?? throw new InvalidDataException("Recording unavailable.");
        var audio = evidence["audioAvailable"]?.GetValue<bool>() == true;
        if (audio)
        {
            Button(_startMs.HasValue ? $"Play citation at {TimeSpan.FromMilliseconds(_startMs.Value):g}" : "Play recording", () => PlayAsync(_startMs ?? 0));
            Button("Pause / resume", () =>
            {
                if (_player?.IsPlaying == true) _player.Pause(); else _player?.Start();
                return Task.CompletedTask;
            });
        }
        if (_client.IsOwner)
        {
            Button(audio ? "Replace audio" : "Attach audio", () => TransferAsync("attach"));
            if (audio) Button("Delete audio", async () =>
            {
                if (!await DisplayAlertAsync(Title, "Delete the stored recording audio?", "Delete", "Cancel")) return;
                ReleasePlayer();
                await _client.TransferAsync("delete", _id, null, null);
                await RenderAsync();
            });
        }
        Button("Share transcript", async () =>
        {
            var path = Path.Combine(FileSystem.CacheDirectory, $"transcript-{_id}.txt");
            await using (var file = File.Create(path)) await _client.TransferAsync("transcript", _id, null, file);
            await Share.Default.RequestAsync(new ShareFileRequest("Share transcript", new ShareFile(path)));
        });
        var transcript = evidence["transcript"]!;
        var utterances = transcript["utterances"]!.AsArray();
        if (utterances.Count == 0) Label(Text(transcript, "transcriptText"));
        foreach (var item in utterances)
        {
            var start = item!["startMs"]!.GetValue<int>();
            var heading = $"{TimeSpan.FromMilliseconds(start):g} · {Text(item, "speakerName")} {Text(item, "speakerRole")}";
            if (audio) Button(heading, () => PlayAsync(start)); else Label(heading, true);
            Label(Text(item, "text"));
        }
    }
}
