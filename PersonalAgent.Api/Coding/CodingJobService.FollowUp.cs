using PersonalAgent.Api.Models;
using PersonalAgent.Contracts.Coding;
using PersonalAgent.Integrations;

namespace PersonalAgent.Api.Coding;

internal sealed partial class CodingJobService
{
    internal static Guid? ConversationId(AgentAccessContext Access) =>
        Access.ScheduledRun is null && Guid.TryParse(Access.SessionId, out var Session) ? Session : null;

    private async Task<(CodingJob Parent, CodingRevisionTarget Target)?> PrepareRevisionAsync(
        AgentAccessContext Access, StartCodingJob Request, CodingSettings Settings, CancellationToken Token)
    {
        if (Request.PreviousJobId is not { } Previous)
        {
            if (Request.FollowUp is not null) throw new ArgumentException("A follow-up requires the job ID of the existing pull request.");
            return null;
        }
        if (Request.FollowUp is not ("ci" or "review" or "rebase")) throw new ArgumentException("Choose ci, review or rebase for a pull request follow-up.");
        if (Request is { FollowUp: "rebase", AllowNoChanges: true })
            throw new ArgumentException("A rebase follow-up must publish its validated branch update.");
        var Parent = await Store.GetAsync(Previous, Token);
        if (Parent is null || Parent.ActorId != Access.ActorId || Parent.Subject != Access.SubjectProfileId)
            throw new KeyNotFoundException("Coding job not found.");
        if (Parent.Status != "PrOpened" || Parent.PullRequestUrl is null || Parent.Settings.Repository != Settings.Repository
            || Parent.Settings.GitHubAppId != Settings.GitHubAppId || Parent.Settings.InstallationId != Settings.InstallationId)
            throw new InvalidOperationException("Choose a published coding job for the configured repository and GitHub App.");
        return (Parent, await Publisher.PrepareRevisionAsync(Settings, Parent, Request.FollowUp, Token));
    }

    public async Task<IReadOnlyList<CodingQuestion>> QuestionsAsync(AgentAccessContext Access, Guid? Session, CancellationToken Token)
    {
        await RequireAsync(Access.ActorId, Access.Email, Access.SubjectProfileId, Token);
        return await Store.QuestionsAsync(Access.ActorId, Access.SubjectProfileId, Session, false, Token);
    }

    public async Task<object> AnswerQuestionAsync(AgentAccessContext Access, Guid Id, string Answer, bool InChat, CancellationToken Token)
    {
        await RequireAsync(Access.ActorId, Access.Email, Access.SubjectProfileId, Token);
        if (string.IsNullOrWhiteSpace(Answer) || Answer.Length > 4000)
            throw new ArgumentException("Enter an answer of 1–4000 characters.");
        var Session = InChat ? ConversationId(Access) ?? throw new UnauthorizedAccessException() : (Guid?)null;
        if (!await Store.AnswerAsync(Id, Access.ActorId, Access.SubjectProfileId, Session, Answer.Trim(), Token))
            throw new InvalidOperationException("This question is no longer awaiting your answer.");
        return new { questionId = Id, message = "Answer delivered to the coding job." };
    }

    public async Task<CodingQuestion?> AskQuestionAsync(Guid Id, string Header, AskCodingQuestion Request, CancellationToken Token)
    {
        if (!Header.StartsWith("Bearer ", StringComparison.Ordinal) || Header.Length != 71)
            throw new UnauthorizedAccessException();
        var Job = await Store.GetAsync(Id, Token) ?? throw new UnauthorizedAccessException();
        if (!await Store.AuthorizeModelRequestAsync(Id, Header[7..], Token))
            throw new UnauthorizedAccessException();
        await RequireAsync(Job.ActorId, Job.Email, Job.Subject, Token);
        if (!(await Store.SettingsAsync(Token)).View.Settings.Enabled || Job.SessionId is null)
            throw new UnauthorizedAccessException();
        if (Request.Id == Guid.Empty || string.IsNullOrWhiteSpace(Request.Text) || Request.Text.Length > 2000)
            throw new ArgumentException("Enter a question of 1–2000 characters.");
        return await Store.AskAsync(Id, Header[7..], Request with { Text = Request.Text.Trim() }, Token);
    }
}
