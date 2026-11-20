using MassTransit;

using PersonalAgent.Api.Services;
using PersonalAgent.Contracts.Messaging.Events;
using PersonalAgent.Integrations;

namespace PersonalAgent.Api.Coding;

internal sealed class CodingConversation(CodingJobStore Store, PostgresAgentSessionStore Sessions,
    IBus Bus, PushNotificationService Push)
{
    public async Task DeliverAsync(CancellationToken Token)
    {
        await DeliverResultsAsync(Token);
        await DeliverQuestionsAsync(Token);
    }

    private async Task DeliverQuestionsAsync(CancellationToken Token)
    {
        foreach (var Question in await Store.QuestionsAsync(null, null, null, true, Token))
        {
            var Job = await Store.GetAsync(Question.JobId, Token);
            if (Job is null) continue;
            var Open = Job.Status == "Running" && Question.Answer is null && Question.Deadline > DateTimeOffset.UtcNow;
            var Reply = Open
                ? $"Reply here or use [Outstanding questions]({Dashboard(Job)}#outstanding-questions)."
                : Question.Answer is null ? "The coding job ended before an answer arrived." : "This question has been answered.";
            var Text = $"""
                **OpenCode question**

                {Quote(Question.Text)}

                {Reply} Question ID: `{Question.Id:D}`.
                """;
            var Posted = await Sessions.PostCodingMessageAsync(Question.SessionId, Job.ActorId, Job.Subject, Question.Id, Text, Token);
            if (Posted is null) continue;
            if (Posted.Value && Push.IsEnabled && Open)
            {
                await Bus.Publish(new DevicePushNotificationRequested
                {
                    NotificationId = Question.Id, RequestedAt = DateTimeOffset.UtcNow, ExpiresAt = Question.Deadline,
                    ProfileId = Job.Subject, NotificationType = "coding-question", Title = "OpenCode needs your input",
                    Body = "Open Outstanding questions to answer a coding question.",
                    Data = new Dictionary<string, string> { ["codingQuestion"] = Question.Id.ToString("D"),
                        ["sessionId"] = Question.SessionId.ToString("D"), ["deepLink"] = Dashboard(Job) + "#outstanding-questions" }
                }, Context => Context.MessageId = Question.Id, Token);
            }
            await Store.MarkConversationPostAsync(Question.Id, true, Token);
        }
    }

    private async Task DeliverResultsAsync(CancellationToken Token)
    {
        foreach (var Job in await Store.UnpostedResultsAsync(Token))
        {
            if (Job.SessionId is not { } Session) continue;
            var State = Job.Status == "PrOpened" ? Job.RootJobId is null ? "Draft PR opened" : "Pull request updated" : Job.Status;
            var Checks = Job.Artifact?.Checks.Select(Check => $"- `{Check.Command.Replace("`", "")}` (exit {Check.ExitCode})") ?? [];
            var Summary = Job.Status == "PrOpened" && Job.Artifact?.Summary is { } Report
                ? Quote(Report[..Math.Min(Report.Length, 600)]) : "";
            var Text = $"""
                **Coding job: {State}**

                Job ID: `{Job.Id:D}`

                {(Job.PullRequestUrl is { } Url ? $"[Open pull request]({Url})\n\n" : "")}{Job.Error}

                {Summary}

                {string.Join('\n', Checks)}

                [View coding job]({Dashboard(Job)})
                """;
            if (await Sessions.PostCodingMessageAsync(Session, Job.ActorId, Job.Subject, Job.Id, Text, Token) is null) continue;
            await Store.MarkConversationPostAsync(Job.Id, false, Token);
        }
    }

    private static string Dashboard(CodingJob Job) => "/coding-jobs?profileId=" + Uri.EscapeDataString(Job.Subject);
    private static string Quote(string Text) => string.Join('\n', Text.Replace("\r", "").Split('\n').Select(Line => "    " + Line));
}
