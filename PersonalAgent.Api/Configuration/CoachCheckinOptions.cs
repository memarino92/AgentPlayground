namespace PersonalAgent.Api.Configuration;

internal record CoachCheckinOptions
{
    public const string SectionName = "CoachCheckins";

    public int MaxUploadMb { get; set; } = 25;
    public int FailedUploadRetentionDays { get; set; } = 7;
    public string CoachName { get; set; } = "Andrew";
    public string AthleteName { get; set; } = "Michael";
}
