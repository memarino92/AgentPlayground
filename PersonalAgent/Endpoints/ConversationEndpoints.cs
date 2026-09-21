using AgentPlayground.Contracts;
using Microsoft.Extensions.Options;
using PersonalAgent.Configuration;
using PersonalAgent.Models;
using PersonalAgent.Security;
using PersonalAgent.Services;

namespace PersonalAgent.Endpoints;

internal static partial class PersonalAgentEndpoints
{
    private static void MapConversationEndpoints(RouteGroupBuilder Api, IOptions<SecurityOptions> Security)
    {
        var Group = Api.MapGroup("/conversation").AddEndpointFilter(new SignedActorFilter(Security));
        Group.MapGet("/history", async (HttpContext Context, string profileId, string query, [Microsoft.AspNetCore.Mvc.FromServices] IConversationContextStore Store,
            ICoachAssignmentStore Assignments, CancellationToken Token) =>
        {
            if (string.IsNullOrWhiteSpace(query) || query.Length > 500) return Results.BadRequest();
            var Access = await ResolveAccessAsync(Context, profileId, Assignments);
            if (Access is null) return Results.StatusCode(403);
            var History = await Store.SearchHistoryAsync(Access, Guid.Empty, long.MaxValue, null, query, null, Token);
            return Results.Ok(History.Select(Turn => Turn.Source));
        });
        Group.MapPost("/open", async (HttpContext Context, CreateSessionRequest Request, AgentChatService Chat, ICoachAssignmentStore Assignments, CancellationToken Token) =>
        {
            var Access = await ResolveAccessAsync(Context, Request.ProfileId, Assignments);
            if (Access is null) return Results.StatusCode(403);
            return Results.Ok(await Chat.OpenConversationAsync(Access, Request.ModelId, Token));
        });
        Group.MapPost("/{Id:guid}/clear", async (HttpContext Context, Guid Id, CreateSessionRequest Request,
            AgentChatService Chat, ICoachAssignmentStore Assignments, CancellationToken Token) =>
        {
            var Access = await ResolveAccessAsync(Context, Request.ProfileId, Assignments);
            if (Access is null) return Results.StatusCode(403);
            return await Chat.ClearConversationAsync(Id, Access, Token) ? Results.NoContent() : Results.NotFound();
        });
        Group.MapPost("/{Id:guid}/messages", async (HttpContext Context, Guid Id, SendMessageRequest Request,
            AgentChatService Chat, ICoachAssignmentStore Assignments, CancellationToken Token) =>
        {
            if (string.IsNullOrWhiteSpace(Request.Message) || Request.Message.Length > PersonalAgentConstants.MaxMessageLength)
                return Results.BadRequest(new { error = "A message within the supported length is required." });
            var Access = await ResolveAccessAsync(Context, Request.ProfileId, Assignments);
            if (Access is null) return Results.StatusCode(403);
            if (await Chat.ReadConversationAsync(Id, Access, Token) is null) return Results.NotFound();
            try
            {
                var Reply = await Chat.SendMessageAsync(Id.ToString(), Access, Request.Message, Token);
                if (Reply is null) return Results.NotFound();
                return Results.Ok(await Chat.ReadConversationAsync(Id, Access, Token));
            }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
        });
        Group.MapPost("/{Id:guid}/cards/{Sequence:long}/{CardId}", async (HttpContext Context, Guid Id, long Sequence, string CardId,
            UpdateChatCardRequest Request, AgentChatService Chat, ICoachAssignmentStore Assignments, CancellationToken Token) =>
        {
            if (Request.Action is null) return Results.BadRequest();
            var Access = await ResolveAccessAsync(Context, Request.ProfileId, Assignments);
            if (Access is null) return Results.StatusCode(403);
            try
            {
                var Updated = await Chat.UpdateCardAsync(Id, Sequence, CardId, Request.Action, Access, Token);
                return Updated is null ? Results.Conflict(new { error = "This card changed or that action is no longer available. Reopen chat to refresh." }) : Results.Ok(Updated);
            }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
        });
    }

    internal record UpdateChatCardRequest(string ProfileId, ChatCardAction Action);
}
