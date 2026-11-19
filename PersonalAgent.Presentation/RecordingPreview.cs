namespace PersonalAgent.Presentation;

public static class RecordingPreview
{
    public static readonly Guid CompletedId = Guid.Parse("820df9dc-67c4-44f2-aad8-dbf516884341");
    public static readonly Guid ReviewId = Guid.Parse("a184f22e-223e-411c-809d-6c11ae4daf68");

    public static List<CoachCheckinAdminItemResponse> Recordings()
    {
        var now = DateTimeOffset.UtcNow;
        return
        [
            new CoachCheckinAdminItemResponse(
                ReviewId, Guid.NewGuid(), "demo-athlete", "check-in-awaiting-review.m4a", "AwaitingSpeakerOverride", null,
                now.AddHours(-3), now.AddHours(-2), true, 18, 0,
                [new(0, "unknown", 10, ["My knee feels much better after the warm-up.", "I can fit in the recovery walk on Wednesday."]), new(1, "unknown", 8, ["Keep the tempo controlled and leave two reps in reserve."])]),
            new CoachCheckinAdminItemResponse(
                CompletedId, Guid.NewGuid(), "demo-athlete", "weekly-check-in.m4a", "Completed", null,
                now.AddDays(-1), now.AddHours(-5), false, 42, 11,
                [new(0, "coach", 19, ["Walk me through how your knee felt."]), new(1, "athlete", 23, ["I felt stable during the warm-up."])]),
            new CoachCheckinAdminItemResponse(
                Guid.NewGuid(), Guid.NewGuid(), "demo-athlete", "friday-call.m4a", "Transcribing", null,
                now.AddMinutes(-20), now.AddMinutes(-4), true, 0, 0, [])
        ];
    }

    public static List<CoachCheckinTranscriptUtteranceResponse> BuildPreviewUtterances()
    {
        string[] coachLines =
        [
            "Walk me through how your knee felt after last week's session.",
            "Good. Keep the tempo controlled and leave two reps in reserve this week.",
            "What changed between the sets that felt stable and the ones that felt less predictable?",
            "Let's keep the warm-up consistent so we can tell whether the adjustment is working.",
            "I want you to log the first working set and note any discomfort before moving on.",
            "That is a sensible progression. We can add load only if the technique stays identical.",
            "For this week, prioritize a clean range of motion over chasing the previous number.",
            "We will review the recovery walk, sleep, and how the knee responds the following morning."
        ];
        string[] athleteLines =
        [
            "Much better. I felt stable during the warm-up and had no pain after the squats.",
            "That sounds good. I can also fit in the extra recovery walk on Wednesday.",
            "The right side still felt a little tight on the first set, but it settled down quickly.",
            "I noticed I was more confident when I slowed down at the bottom of the movement.",
            "I slept well after the session and did not have the usual stiffness when I got up.",
            "I can record the working sets and keep the load where it is for another week.",
            "The recovery walk felt easy, and I think I can repeat that on the days between sessions.",
            "I will send notes after the next session so we can compare the response."
        ];

        var utterances = new List<CoachCheckinTranscriptUtteranceResponse>();
        for (var index = 0; index < 72; index++)
        {
            var isCoach = index % 2 == 0;
            var startMs = index * 24_000;
            utterances.Add(new CoachCheckinTranscriptUtteranceResponse(
                isCoach ? 0 : 1,
                isCoach ? "coach" : "athlete",
                startMs,
                startMs + 20_000,
                isCoach ? coachLines[(index / 2) % coachLines.Length] : athleteLines[(index / 2) % athleteLines.Length],
                isCoach ? 0.97 : 0.94));
        }

        return utterances;
    }
}
