namespace PersonalAgent.Worker.Services;

internal sealed class TranscriptionFailedException(string Message) : InvalidOperationException(Message);
