namespace PersonalAgent.Worker.Configuration;

public record ApiKeyOptions
{
    public string OpenAiKey { get; set; } = string.Empty;
}
