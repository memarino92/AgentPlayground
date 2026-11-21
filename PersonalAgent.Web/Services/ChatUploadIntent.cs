namespace PersonalAgent.Web.Services;

internal static class ChatUploadIntent
{
    // The current file-picker action plus the owner's explicit product preference authorize a context-free .m4a.
    // History may only make this more conservative, never authorize an operation.
    public static bool AutoSubmitCoaching(string Draft, string LastUserMessage) => string.IsNullOrWhiteSpace(Draft)
        && !new[] { "sandbox", "do not transcribe", "don't transcribe", "not coaching", "not for coaching", "do not upload", "don't upload" }
            .Any(Cue => LastUserMessage.Contains(Cue, StringComparison.OrdinalIgnoreCase));
}
