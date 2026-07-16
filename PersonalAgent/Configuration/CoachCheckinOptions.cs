namespace PersonalAgent.Configuration;

internal record CoachCheckinOptions
{
    public const string SectionName = "CoachCheckins";

    public int MaxUploadMb { get; set; } = 25;
    public int FailedUploadRetentionDays { get; set; } = 7;
}
