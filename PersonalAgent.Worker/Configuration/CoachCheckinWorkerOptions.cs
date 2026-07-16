namespace PersonalAgent.Worker.Configuration;

internal record CoachCheckinWorkerOptions
{
    public const string SectionName = "CoachCheckins";

    public string Schema { get; set; } = "agent_memory";
    public int FailedUploadRetentionDays { get; set; } = 7;
}
