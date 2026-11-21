namespace PersonalAgent.Mobile.Services;

// Owns only a submitted draft; response streaming must not own the next draft.
public sealed class ChatComposer
{
    public bool IsSending { get; private set; }

    public async Task SendAsync(Func<string?> ReadDraft, Action<string> WriteDraft, Func<string, Task> Send)
    {
        if (IsSending) return;
        var Draft = ReadDraft();
        if (string.IsNullOrWhiteSpace(Draft)) return;
        IsSending = true;
        try
        {
            WriteDraft(string.Empty);
            await Send(Draft.Trim());
        }
        catch
        {
            // An uncertain delivery still needs a refresh before retrying. Never replace
            // text entered while waiting; the optimistic turn retains that failed message.
            if (string.IsNullOrEmpty(ReadDraft())) WriteDraft(Draft);
            throw;
        }
        finally { IsSending = false; }
    }
}
