namespace PersonalAgent.Configuration;

internal record AssemblyAiOptions
{
    public const string SectionName = "AssemblyAi";

    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.assemblyai.com/v2";
    public List<string> SpeechModels { get; set; } = ["universal-3-5-pro"];
    public int PollIntervalSeconds { get; set; } = 4;
    public int TranscriptionTimeoutMinutes { get; set; } = 15;
}
