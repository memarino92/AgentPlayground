using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PersonalAgent.Api.Configuration;
using PersonalAgent.Api.Models;
using PersonalAgent.Api.Security;
using PersonalAgent.Api.Services;
using PersonalAgent.Integrations.ChatGpt;

namespace PersonalAgent.Api.Endpoints;

internal static class ChatGptEndpoints
{
    public static void MapChatGpt(this RouteGroupBuilder Api, IOptions<SecurityOptions> Security)
    {
        var account = Api.MapGroup("/chatgpt").AddEndpointFilter(new SignedActorFilter(Security, ownerOnly: true));
        account.MapGet("/connections", (HttpContext Context, [FromServices] ChatGptConnectionStore Store, CancellationToken Token) =>
            ExecuteAsync(async () => Results.Ok(await Store.ListAsync(SignedActorFilter.Get(Context).ActorId, Token))));
        account.MapPost("/pairings", (HttpContext Context, ChatGptPairingRequest Request, [FromServices] ChatGptConnectionStore Store, CancellationToken Token) =>
            ExecuteAsync(async () =>
            {
                Context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(await Store.StartAsync(SignedActorFilter.Get(Context).ActorId, Request.ConnectionId, Token));
            }));
        account.MapDelete("/connections/{id:guid}", (HttpContext Context, Guid Id, [FromServices] ChatGptConnectionStore Store, CancellationToken Token) =>
            ExecuteAsync(async () => Results.Ok(await Store.DisconnectAsync(SignedActorFilter.Get(Context).ActorId, Id, Token))));
        account.MapPost("/connections/{id:guid}/welcome", (HttpContext Context, Guid Id, [FromServices] ChatGptConnectionStore Store, CancellationToken Token) =>
            ExecuteAsync(async () =>
            {
                await Store.DismissWelcomeAsync(SignedActorFilter.Get(Context).ActorId, Id, Token);
                return Results.NoContent();
            }));
        // Coaches retain the global catalog; their owner's account credentials are never exposed.
        Api.MapGet("/chatgpt/models", (HttpContext Context, [FromServices] ChatGptModelCatalog Catalog, [FromServices] IChatModelCatalog Global, CancellationToken Token) =>
            ExecuteAsync(async () => Results.Ok(new ChatModelsResponse(SignedActorFilter.Get(Context).Role == AgentRoles.Owner
                ? await Catalog.GetModelsAsync(SignedActorFilter.Get(Context).ActorId, Token) : await Global.GetModelsAsync(Token)))))
            .AddEndpointFilter(new SignedActorFilter(Security));
        // The internal API key is still required. A pairing code is the one-time capability from an authenticated owner.
        Api.MapPost("/chatgpt/handshake", (HttpContext Context, ChatGptHandshake Request, [FromServices] ChatGptConnectionStore Store, CancellationToken Token) =>
            ExecuteAsync(async () =>
            {
                Context.Response.Headers.CacheControl = "no-store";
                if (string.IsNullOrWhiteSpace(Request.Code) || Request.Code.Length > 128) return Results.BadRequest();
                if (Request.Credentials is null) return Results.Ok(await Store.PrepareAsync(Request.Code, Token));
                await Store.CompleteAsync(Request.Code, Request.Credentials, Token);
                return Results.NoContent();
            }));
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> Action)
    {
        try { return await Action(); }
        catch (ChatGptException Exception) { return Results.Problem(Exception.Message, statusCode: 400); }
    }
}
